import { Component, OnInit, untracked, computed, effect, inject, input, output, signal } from '@angular/core';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { FoldComponent } from '../../../ui/fold.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { PageChrome } from '../shell/page-chrome';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCustomer, AdminProduct, adminErrorMessage } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import {
  ReturnEntry,
  countsReturnDayByDefault,
  lateDeductionProblem,
  lateWaiverProblem,
  localTime,
  returnDayProblem,
  returnProblem,
  transportMissing,
  zonedTimeToUtcIso,
} from '../../../core/rentals';
import { localizedName } from '../backoffice-format';
import { TouchedFields } from '../backoffice-forms';
import {
  ContractDocuments,
  RentalContractLine,
  RentalSettings,
  ReturnNote,
  ReturnNoteLineRequest,
  ReturnNoteRequest, ReturnPreview, SameDayUsage,
  RentalsApi,
} from './rentals-api.service';
import { formatRentalDay, lineName, nowTime, today } from './rental-view';
import { MaintenanceCustomerApi } from '../maintenance/maintenance-customer-api.service';
import { roundTo } from '../receivables/receivable-view';

/** What one line of the note being written says came back, and in what condition. */
interface DraftLine {
  sameDayUsage: SameDayUsage | null;
  qtyOk: number;
  qtyPartialDamage: number;
  maintenanceValue: number;
  qtyTotalDamage: number;
  qtyMissing: number;
  reportedOn: string;
}

/** The id the draft note carries while it exists only on this screen. */
const DRAFT = 'draft';

const BLANK: DraftLine = {
  sameDayUsage: null,
  qtyOk: 0,
  qtyPartialDamage: 0,
  maintenanceValue: 0,
  qtyTotalDamage: 0,
  qtyMissing: 0,
  reportedOn: '',
};

/** Online-only return draft. All financial previews are calculated by the server. */
@Component({
  selector: 'app-rental-return-note',
  standalone: true,
  imports: [FoldComponent, FormsModule, DateInputComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      @if (docs(); as d) {
        <!-- The title is the page header's; the one Cancel is in the action bar below. -->
        <section class="bo-panel form-panel">
          <div class="bo-panel__head">
            <p class="bo-meta">
              <span class="mono">{{ d.contract.number }}</span>
              <span>{{ d.contract.customerName || '—' }}</span>
            </p>
          </div>

          <div class="bo-panel__body bo-form">
            <app-fold class="bo-group" [heading]="i18n.t('rent.return.when')" [open]="true">
              <div class="bo-fields bo-fields--roomy">
                <!-- The contract's branch, for everyone (the owner, 2026-09-30: "the return is to the
                     same branch, then it is transferred to another branch"). Not a choice. -->
                <div class="bo-field" [class.is-invalid]="otherBranch()">
                  <p class="bo-fixed-label">{{ i18n.t('rent.return.takenAt') }}</p>
                  <p class="bo-fixed" id="rr-fixed">{{ contractBranchName() }}</p>
                  <span class="bo-hint">{{ i18n.t('rent.return.atContractHint') }}</span>
                </div>
                <div class="bo-field">
                  <label for="rr-date">{{ i18n.t('rent.returnedOn') }}</label>
                  <app-date-input id="rr-date" [ngModel]="returnedOn()" (ngModelChange)="setReturnDate($event)" />
                </div>
                <div class="bo-field">
                  <label for="rr-time">{{ i18n.t('rent.day.returnTime') }}</label>
                  <input id="rr-time" type="time" class="mono" [ngModel]="returnedTime()" (ngModelChange)="returnedTime.set($event)" />
                </div>
                <div class="bo-field bo-field--wide">
                  <label for="rr-note">
                    {{ i18n.t('rent.notes') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <input id="rr-note" maxlength="500" [ngModel]="note()" (ngModelChange)="note.set($event)" />
                </div>
              </div>
              @if (otherBranch()) {
                <p class="bo-error" role="alert" id="rr-other-branch">
                  {{ i18n.t('rent.return.otherBranch', { branch: contractBranchName() }) }}
                </p>
              }
            </app-fold>

            <!-- Folded: the rule decides the return day and departing from it is the exception. Its
                 answer stays in words under the heading; a missing reason opens it on save. -->
            <app-fold class="bo-group" [heading]="i18n.t('rent.day.returnTitle')">
              <p class="bo-group__note" foldLead>
                <span class="bo-state" [class.bo-state--lead]="countReturnDay()">
                  {{ i18n.t(countReturnDay() ? 'rent.day.counted' : 'rent.day.notCounted') }}
                </span>
                {{ ruleSentence() }}
              </p>
              <div class="bo-check">
                <input
                  id="rr-count-day"
                  type="checkbox"
                  [ngModel]="countReturnDay()"
                  (ngModelChange)="decision.set($event)"
                />
                <label for="rr-count-day">{{ i18n.t('rent.day.countIt') }}</label>
              </div>
              @if (departsFromRule()) {
                <div class="bo-fields bo-fields--roomy">
                  <div class="bo-field bo-field--wide" [class.is-invalid]="reasonInvalid()">
                    <label for="rr-day-reason">
                      {{ i18n.t('rent.day.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <input
                      id="rr-day-reason"
                      maxlength="300"
                      [ngModel]="reason()"
                      (ngModelChange)="reason.set($event)"
                      (blur)="touched.touch('reason')"
                    />
                    <p class="bo-hint">{{ i18n.t('rent.day.reasonHint') }}</p>
                    @if (reasonInvalid()) {
                      <span class="bo-error" role="alert">{{ i18n.t('rent.day.reasonRequired') }}</span>
                    }
                  </div>
                </div>
              }
            </app-fold>

            <!-- Back after the due time (owner, 2026-10-08): said in words, then asked — charge the
                 late days or not. Shown only while the engine would charge this note overdue. Never
                 folded: it is a question that must be answered. -->
            @if (late(); as l) {
              <div class="bo-group" id="rr-late">
                <h3 class="bo-group__title">{{ i18n.t('rent.late.title') }}</h3>
                <p class="bo-group__note">
                  <span class="bo-state bo-state--warn">{{ i18n.t('rent.late.state') }}</span>
                  {{ lateSentence() }}
                </p>
                <div class="late-choice" role="radiogroup" aria-labelledby="rr-late-question" aria-describedby="rr-late-hint">
                  <p class="late-choice__question" id="rr-late-question">{{ i18n.t('rent.late.question', { n: l.days }) }}</p>
                  <div class="bo-check">
                    <input
                      id="rr-late-charge"
                      type="radio"
                      name="rr-late"
                      [checked]="lateAnswer() === 'charge'"
                      (change)="lateAnswer.set('charge')"
                    />
                    <label for="rr-late-charge">{{ i18n.t('rent.late.charge', { v: i18n.money(l.amount) }) }}</label>
                  </div>
                  <div class="bo-check">
                    <input
                      id="rr-late-deduct"
                      type="radio"
                      name="rr-late"
                      [checked]="lateAnswer() === 'deduct'"
                      (change)="lateAnswer.set('deduct')"
                    />
                    <label for="rr-late-deduct">{{ i18n.t('rent.late.deduct') }}</label>
                  </div>
                  <div class="bo-check">
                    <input
                      id="rr-late-waive"
                      type="radio"
                      name="rr-late"
                      [checked]="lateAnswer() === 'waive'"
                      (change)="lateAnswer.set('waive')"
                    />
                    <label for="rr-late-waive">{{ i18n.t('rent.late.waive') }}</label>
                  </div>
                </div>
                <p class="bo-hint" id="rr-late-hint">{{ i18n.t('rent.late.hint') }}</p>
                @if (lateUnanswered()) {
                  <span class="bo-error" role="alert">{{ i18n.t('rent.late.answerRequired') }}</span>
                }
                <!-- Yes, less a deduction (owner, 2026-10-22): in days or as an amount, the branch
                     chooses, and never more than the lateness. Only the deduction travels; the
                     figures beside it are the server's preview. -->
                @if (lateAnswer() === 'deduct') {
                  <div class="late-choice" role="radiogroup" aria-labelledby="rr-late-by">
                    <p class="late-choice__question" id="rr-late-by">{{ i18n.t('rent.late.deductBy') }}</p>
                    <div class="bo-check">
                      <input id="rr-late-by-days" type="radio" name="rr-late-by"
                        [checked]="deductBy() === 'days'" (change)="deductIn('days')" />
                      <label for="rr-late-by-days">{{ i18n.t('rent.late.byDays') }}</label>
                    </div>
                    <div class="bo-check">
                      <input id="rr-late-by-amount" type="radio" name="rr-late-by"
                        [checked]="deductBy() === 'amount'" (change)="deductIn('amount')" />
                      <label for="rr-late-by-amount">{{ i18n.t('rent.late.byAmount') }}</label>
                    </div>
                  </div>
                  <div class="bo-fields bo-fields--roomy">
                    <div class="bo-field" [class.is-invalid]="lateDeductionError()">
                      <label for="rr-late-deduction">
                        {{ i18n.t(deductBy() === 'days' ? 'rent.late.deductDays' : 'rent.late.deductAmount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                      </label>
                      <input
                        id="rr-late-deduction"
                        type="number"
                        inputmode="decimal"
                        [min]="deductBy() === 'days' ? 1 : 0"
                        [max]="deductBy() === 'days' ? l.days : l.amount"
                        [step]="deductBy() === 'days' ? 1 : 'any'"
                        [ngModel]="lateDeduction()"
                        (ngModelChange)="lateDeduction.set($event)"
                        (blur)="touched.touch('lateDeduction')"
                        aria-describedby="rr-late-deduction-hint"
                        [attr.aria-invalid]="lateDeductionError() ? 'true' : null"
                      />
                      <span class="bo-hint" id="rr-late-deduction-hint">{{ deductBy() === 'days'
                        ? i18n.t('rent.late.deductDaysHint', { n: l.days })
                        : i18n.t('rent.late.deductAmountHint', { v: i18n.money(l.amount) }) }}</span>
                      @if (lateDeductionError(); as message) {
                        <span class="bo-error" role="alert">{{ message }}</span>
                      }
                    </div>
                  </div>
                  @if (deductionResult(); as r) {
                    <p class="bo-group__note" role="status">
                      {{ i18n.t('rent.late.deductionResult', { charged: i18n.money(r.charged), waived: i18n.money(r.waived) }) }}
                    </p>
                  }
                }
                @if (lateAnswer() === 'waive' || lateAnswer() === 'deduct') {
                  <div class="bo-fields bo-fields--roomy">
                    <div class="bo-field bo-field--wide" [class.is-invalid]="lateReasonInvalid()">
                      <label for="rr-late-reason">
                        {{ i18n.t('rent.day.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                      </label>
                      <input
                        id="rr-late-reason"
                        maxlength="300"
                        [ngModel]="lateReason()"
                        (ngModelChange)="lateReason.set($event)"
                        (blur)="touched.touch('lateReason')"
                      />
                      @if (lateReasonInvalid()) {
                        <span class="bo-error" role="alert">{{ i18n.t(lateAnswer() === 'deduct' ? 'rent.late.deductReasonRequired' : 'rent.late.reasonRequired') }}</span>
                      }
                    </div>
                  </div>
                }
              </div>
            }

            <!-- Open: the driver and the truck are required whenever the branch transports. -->
            <app-fold class="bo-group" [heading]="i18n.t('rent.truck.noteTitle')" [open]="true">
              <p class="bo-group__note">{{ i18n.t('rent.truck.ownVehicle') }}</p>
              <!-- Owner, 2026-10-02: the driver and the truck are mandatory when the branch
                   transports; when the customer carries the goods the note says so and asks none. -->
              <div class="bo-check">
                <input
                  id="rr-by-customer"
                  type="checkbox"
                  [ngModel]="customerTransport()"
                  (ngModelChange)="customerTransport.set($event)"
                />
                <label for="rr-by-customer">{{ i18n.t('rent.truck.byCustomer') }}</label>
              </div>
              @if (!customerTransport()) {
              <div class="bo-fields bo-fields--roomy">
                <div class="bo-field" [class.is-invalid]="transportInvalid('driverName')">
                  <label for="rr-driver">
                    {{ i18n.t('rent.truck.driverName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="rr-driver"
                    maxlength="120"
                    [ngModel]="driverName()"
                    (ngModelChange)="driverName.set($event)"
                    (blur)="touched.touch('driverName')"
                  />
                  @if (transportInvalid('driverName')) {
                    <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="transportInvalid('driverPhone')">
                  <label for="rr-driver-phone">
                    {{ i18n.t('rent.truck.driverPhone') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="rr-driver-phone"
                    dir="ltr"
                    class="mono"
                    maxlength="40"
                    [ngModel]="driverPhone()"
                    (ngModelChange)="driverPhone.set($event)"
                    (blur)="touched.touch('driverPhone')"
                  />
                  @if (transportInvalid('driverPhone')) {
                    <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
                <div class="bo-field" [class.is-invalid]="transportInvalid('truckNumber')">
                  <label for="rr-truck">
                    {{ i18n.t('rent.truck.number') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="rr-truck"
                    dir="ltr"
                    class="mono"
                    maxlength="40"
                    [ngModel]="truckNumber()"
                    (ngModelChange)="truckNumber.set($event)"
                    (blur)="touched.touch('truckNumber')"
                  />
                  @if (transportInvalid('truckNumber')) {
                    <span class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
              </div>
              }
            </app-fold>

            <!-- Document Summary (Requirement 11) -->
            <div class="return-doc-summary">
              <div class="return-doc-summary__head">
                <h3 class="return-doc-summary__title">{{ i18n.t('rent.return.summaryTitle') }}</h3>
              </div>
              <div class="return-metrics-strip">
                <div class="metric-box">
                  <span class="metric-label">{{ i18n.t('rent.return.outBefore') }}</span>
                  <span class="metric-num mono">{{ totalOutBefore() }}</span>
                </div>
                <div class="metric-box metric-box--good">
                  <span class="metric-label">{{ i18n.t('rent.return.sound') }}</span>
                  <span class="metric-num mono">{{ totalGood() }}</span>
                </div>
                <div class="metric-box metric-box--repair">
                  <span class="metric-label">{{ i18n.t('rent.return.repairable') }}</span>
                  <span class="metric-num mono">{{ totalRepairable() }}</span>
                </div>
                <div class="metric-box metric-box--damage">
                  <span class="metric-label">{{ i18n.t('rent.return.beyondRepair') }}</span>
                  <span class="metric-num mono">{{ totalDamaged() }}</span>
                </div>
                <div class="metric-box metric-box--missing">
                  <span class="metric-label">{{ i18n.t('rent.return.neverCameBack') }}</span>
                  <span class="metric-num mono">{{ totalMissing() }}</span>
                </div>
                <div class="metric-box metric-box--after">
                  <span class="metric-label">{{ i18n.t('rent.return.outAfter') }}</span>
                  <span class="metric-num mono" [class.metric-num--cleared]="totalOutAfter() === 0">{{ totalOutAfter() }}</span>
                </div>
              </div>
              <div class="return-financial-strip">
                <div class="fin-item">
                  <span class="fin-label">{{ i18n.t('rent.return.repairFees') }}:</span>
                  <span class="fin-value mono">{{ i18n.money(totalMaintenanceValue()) }}</span>
                </div>
                <div class="fin-item">
                  <span class="fin-label">{{ i18n.t('rent.return.damageFees') }}:</span>
                  <span class="fin-value mono">{{ i18n.money(totalCompensationValue()) }}</span>
                </div>
                <div class="fin-item">
                  <span class="fin-label">{{ i18n.t('rent.return.sumRentalAdj') }}:</span>
                  <span class="fin-value mono">{{ moneyOrPending(unusedRent()) }}</span>
                </div>
              </div>
            </div>

            <!-- Top Validation Alert (Requirement 13) -->
            @if (attemptedSave() && linesWithErrors().length > 0) {
              <div class="notice danger" role="alert" id="rr-validation-summary">
                <strong>{{ i18n.t('rent.return.validationErrorsTitle') }}</strong>
                <ul class="validation-error-list">
                  @for (err of linesWithErrors(); track err.line.id) {
                    <li>
                      <button type="button" class="btn-link" (click)="scrollToCard(err.line.id)">
                        <span class="mono">{{ err.line.sku }}</span> — {{ name(err.line) }}: {{ err.errorText }}
                      </button>
                    </li>
                  }
                </ul>
              </div>
            }

            <!-- Compact Item Cards Stack (Requirements 1, 2, 3, 4, 5, 6, 7, 8, 9, 10) -->
            <div class="return-items-stack">
              @for (line of returnable(); track line.id) {
                <article
                  class="return-item-card"
                  [id]="'rc-card-' + line.id"
                  [class.is-invalid]="hasCardError(line)"
                  [class.is-complete]="reconciliationState(line) === 'complete'"
                  [class.is-collapsed]="isCardCollapsed(line.id)"
                >
                  <!-- Card Header -->
                  <header class="return-card-header">
                    <div class="return-card-info">
                      <div class="return-card-title-row">
                        <span class="mono item-sku">{{ line.sku }}</span>
                        <h4 class="item-name">{{ name(line) }}</h4>
                      </div>
                      <div class="return-card-meta">
                        <span class="out-badge">
                          <strong>{{ line.stillOutQty }}</strong> {{ i18n.t('rent.return.stillOutOf', { n: '' }).trim() }}
                        </span>
                        <span class="meta-sep">·</span>
                        <span class="damage-price-hint">
                          {{ i18n.t('rent.return.linePrices', { v: i18n.money(line.damageUnitPrice) }) }}
                        </span>
                      </div>
                    </div>

                    <div class="return-card-actions">
                      <span
                        class="reconcile-pill"
                        [class.reconcile-pill--none]="reconciliationState(line) === 'none'"
                        [class.reconcile-pill--partial]="reconciliationState(line) === 'partial'"
                        [class.reconcile-pill--complete]="reconciliationState(line) === 'complete'"
                        [class.reconcile-pill--exceeds]="reconciliationState(line) === 'exceeds'"
                      >
                        {{ reconciliationText(line) }}
                      </span>

                      <!-- Convenience button (Requirement 5) -->
                      <button
                        type="button"
                        class="btn-convenience"
                        (click)="setAllSound(line)"
                      >
                        ✓ {{ i18n.t('rent.return.allSound') }}
                      </button>

                      @if (allocatedQty(line.id) > 0) {
                        <button
                          type="button"
                          class="btn-toggle-card"
                          (click)="toggleCard(line.id)"
                          [attr.aria-expanded]="!isCardCollapsed(line.id)"
                        >
                          {{ i18n.t(isCardCollapsed(line.id) ? 'rent.return.editCard' : 'rent.return.collapseCard') }}
                        </button>
                      }
                    </div>
                  </header>

                  <!-- Collapsed summary state (Requirement 7) -->
                  @if (isCardCollapsed(line.id)) {
                    <div class="return-card-collapsed-summary">
                      <div class="summary-line">
                        <span class="summary-details">{{ cardSummaryText(line.id) }}</span>
                        @if (draft(line.id).qtyPartialDamage > 0 && draft(line.id).maintenanceValue > 0) {
                          <span class="summary-cost">
                            {{ i18n.t('rent.return.repairCharge') }}: <strong class="mono">{{ i18n.money(draft(line.id).maintenanceValue) }}</strong>
                          </span>
                        }
                      </div>
                      @if (reconciliationState(line) === 'complete') {
                        <span class="bo-state bo-state--ok">✓ {{ i18n.t('common.done') }}</span>
                      }
                    </div>
                  } @else {
                    <!-- Expanded Card Body -->
                    <div class="return-card-body">
                      <!-- 4 Condition inputs in a compact group (Requirement 2) -->
                      <div class="condition-control-group">
                        <div class="condition-col col-sound">
                          <label [attr.for]="'rr-ok' + line.id">{{ i18n.t('rent.return.sound') }}</label>
                          <div class="num-wrapper">
                            <input
                              [attr.id]="'rr-ok' + line.id"
                              type="number"
                              min="0"
                              class="num mono condition-input"
                              [ngModel]="draft(line.id).qtyOk"
                              (ngModelChange)="patch(line.id, { qtyOk: +$event })"
                            />
                          </div>
                        </div>

                        <div class="condition-col col-repairable">
                          <label [attr.for]="'rr-pd' + line.id">{{ i18n.t('rent.return.repairable') }}</label>
                          <div class="num-wrapper">
                            <input
                              [attr.id]="'rr-pd' + line.id"
                              type="number"
                              min="0"
                              class="num mono condition-input"
                              [ngModel]="draft(line.id).qtyPartialDamage"
                              (ngModelChange)="patch(line.id, { qtyPartialDamage: +$event })"
                            />
                          </div>
                        </div>

                        <div class="condition-col col-damaged">
                          <label [attr.for]="'rr-td' + line.id">{{ i18n.t('rent.return.beyondRepair') }}</label>
                          <div class="num-wrapper">
                            <input
                              [attr.id]="'rr-td' + line.id"
                              type="number"
                              min="0"
                              class="num mono condition-input"
                              [ngModel]="draft(line.id).qtyTotalDamage"
                              (ngModelChange)="patch(line.id, { qtyTotalDamage: +$event })"
                            />
                          </div>
                        </div>

                        <!-- Missing visually distinct (Requirement 8) -->
                        <div class="condition-col col-missing">
                          <label [attr.for]="'rr-ms' + line.id">
                            {{ i18n.t('rent.return.neverCameBack') }}
                            <span class="missing-tag">{{ i18n.t('rent.kind.Missing') }}</span>
                          </label>
                          <div class="num-wrapper">
                            <input
                              [attr.id]="'rr-ms' + line.id"
                              type="number"
                              min="0"
                              class="num mono condition-input input-missing"
                              [ngModel]="draft(line.id).qtyMissing"
                              (ngModelChange)="patch(line.id, { qtyMissing: +$event })"
                            />
                          </div>
                        </div>
                      </div>

                      <!-- Progressive disclosure: repair cost (Requirements 4, 9) -->
                      @if (draft(line.id).qtyPartialDamage > 0) {
                        <div class="conditional-field-row progressive-row">
                          <div class="bo-field">
                            <label [attr.for]="'rr-mv' + line.id">
                              {{ i18n.t('rent.return.repairCharge') }}
                              <span class="bo-req">{{ i18n.t('common.required') }}</span>
                            </label>
                            <input
                              [attr.id]="'rr-mv' + line.id"
                              type="number"
                              min="0"
                              class="num mono"
                              [step]="regional.moneyStep()"
                              [ngModel]="draft(line.id).maintenanceValue"
                              (ngModelChange)="patch(line.id, { maintenanceValue: +$event })"
                            />
                            <p class="bo-hint">{{ i18n.t('rent.return.linePrices', { v: i18n.money(draft(line.id).maintenanceValue) }) }}</p>
                            <!-- MS-23 (Phase M5a): a suggestion only, from the product's repair rate; the clerk types the recharge. -->
                            @if (suggestedRepair(line.productId, draft(line.id).qtyPartialDamage); as s) {
                              <p class="bo-hint">
                                {{ i18n.t('maint.rates.suggestion', { n: s.qty, rate: i18n.money(s.rate), amount: i18n.money(s.amount) }) }}
                                <button type="button" class="btn-ghost" (click)="patch(line.id, { maintenanceValue: s.amount })">{{ i18n.t('maint.rates.useSuggestion') }}</button>
                              </p>
                            }
                          </div>
                        </div>
                      }

                      <!-- Progressive disclosure: loss report date (Requirement 4) -->
                      @if (draft(line.id).qtyMissing > 0) {
                        <div class="conditional-field-row progressive-row">
                          <div class="bo-field">
                            <label [attr.for]="'rr-rp' + line.id">{{ i18n.t('rent.return.reportedOn') }}</label>
                            <app-date-input
                              [id]="'rr-rp' + line.id"
                              [disabled]="!pureLoss(line.id)"
                              [ngModel]="draft(line.id).reportedOn"
                              (ngModelChange)="patch(line.id, { reportedOn: $event })"
                            />
                            <p class="bo-hint">
                              {{ i18n.t(pureLoss(line.id) ? 'rent.return.reportedHint' : 'rent.return.reportedOnlyLoss') }}
                              @if (draft(line.id).reportedOn) {
                                ({{ formatDate(draft(line.id).reportedOn) }})
                              }
                            </p>
                          </div>
                        </div>
                      }

                      <!-- Same-day usage rule (Requirement 10) -->
                      @if (usageRequired(line.id)) {
                        <fieldset class="usage-choice" [attr.aria-describedby]="'rr-usage-hint-' + line.id">
                          <legend>{{ i18n.t('rent.usage.question') }}</legend>
                          <label><input type="radio" [name]="'usage-' + line.id" value="Used"
                            [ngModel]="draft(line.id).sameDayUsage" (ngModelChange)="patch(line.id, {sameDayUsage: $event})" />
                            {{ i18n.t('rent.usage.used') }}</label>
                          <label><input type="radio" [name]="'usage-' + line.id" value="NotUsed"
                            [ngModel]="draft(line.id).sameDayUsage" (ngModelChange)="patch(line.id, {sameDayUsage: $event})" />
                            {{ i18n.t('rent.usage.notUsed') }}</label>
                          @if (!draft(line.id).sameDayUsage) {
                            <p class="bo-error" [id]="'rr-usage-hint-' + line.id">{{ i18n.t('rent.usage.required') }}</p>
                          }
                        </fieldset>
                      }

                      <!-- Line error display (Requirement 13) -->
                      @if (cardErrorText(line); as errorMsg) {
                        <div class="line-error-banner" role="alert">
                          <span class="bo-error">{{ errorMsg }}</span>
                        </div>
                      }
                    </div>
                  }
                </article>
              } @empty {
                <div class="bo-group">
                  <p class="bo-group__note">{{ i18n.t('rent.return.nothingOut') }}</p>
                </div>
              }
            </div>
          </div>
        </section>

        @if (previewPending()) { <p role="status">{{ i18n.t('rent.usage.loading') }}</p> }
        @if (previewError(); as message) { <p class="notice danger" role="alert">{{ message }}</p> }
        @if (!previewPending() && preview(); as p) {
          <section class="bo-panel"><div class="bo-panel__body">
            <h2 class="bo-panel__title">{{ i18n.t('rent.usage.preview') }}</h2>
            <p>{{ i18n.t('rent.returnedOn') }}: {{ returnedOn() }} {{ returnedTime() }}</p>
            @for (row of p.rows; track $index) {
              <div class="usage-preview">
                <strong>{{ nameOf(row.contractLineId) }} · {{ row.issueNumber }}</strong>
                <p>{{ i18n.t('rent.usage.issued') }}: {{ i18n.dateTime(row.issuedAtUtc) }}</p>
                <p>{{ i18n.t('register.qty') }}: {{ row.qty }} · {{ i18n.t('rent.unitPrice') }}: {{ i18n.money(row.dailyUnitRate) }}</p>
                @if (row.sameDayUsageRequired) { <p>{{ i18n.t('rent.usage.state') }}:
                  {{ row.sameDayUsage ? i18n.t(row.sameDayUsage === 'Used' ? 'rent.usage.used' : 'rent.usage.notUsed') : '—' }}</p> }
                <p>{{ i18n.t('rent.usage.days') }}: {{ row.chargeableDays ?? '—' }} ·
                  {{ i18n.t('rent.usage.charge') }}: {{ moneyOrPending(row.rentalCharge) }}</p>
              </div>
            }
            <p>{{ i18n.t('rent.usage.paid') }}: {{ i18n.money(p.previouslyPaid) }}</p>
            <p>{{ i18n.t('rent.usage.credit') }}: {{ moneyOrPending(p.customerCredit) }}</p>
            <p class="bo-hint">{{ i18n.t('rent.usage.refundSeparate') }}</p>
            @for (problem of p.problems; track $index) { <p class="bo-error" role="alert">{{ problemText(problem) }}</p> }
          </div></section>
        }
        <!-- ---------- the money, before it is committed ---------- -->
        @if (!previewPending() && preview()) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('rent.return.willCharge') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('rent.return.willChargeHint') }}</p>
          </div>

          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('rent.charge') }}</th>
                  <th>{{ i18n.t('rent.item') }}</th>
                  <th class="num">{{ i18n.t('register.qty') }}</th>
                  <th class="num">{{ i18n.t('rent.days') }}</th>
                  <th class="num">{{ i18n.t('rent.unitPrice') }}</th>
                  <th class="num">{{ i18n.t('common.total') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (charge of charges(); track $index) {
                  <tr>
                    <td [attr.data-label]="i18n.t('rent.charge')">
                      <span class="bo-state" [class]="chargeTone(charge.kind)">{{ i18n.t('rent.kind.' + charge.kind) }}</span>
                    </td>
                    <td [attr.data-label]="i18n.t('rent.item')">{{ nameOf(charge.contractLineId) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('register.qty')">{{ charge.qty }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('rent.days')">{{ charge.days }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('rent.unitPrice')">{{ i18n.money(charge.unitPrice) }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('common.total')">
                      {{ i18n.money(charge.amount) }}
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="6" class="bo-empty">
                      <strong>{{ i18n.t('rent.return.nothingYet') }}</strong>
                      <span>{{ i18n.t('rent.return.nothingYetHint') }}</span>
                    </td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr>
                  <td [attr.data-label]="i18n.t('common.total')">{{ i18n.t('rent.return.thisNote') }}</td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td></td>
                  <td class="num mono" [attr.data-label]="i18n.t('common.total')">{{ moneyOrPending(chargeTotal()) }}</td>
                </tr>
              </tfoot>
            </table>
          </div>

          @if ((unusedRent() ?? 0) > 0) {
            <div class="bo-panel__body">
              <div class="bo-metrics">
                <div class="bo-metric bo-metric--lead">
                  <span class="bo-metric__label">{{ i18n.t('rent.toGiveBack') }}</span>
                  <strong class="bo-metric__value mono">{{ moneyOrPending(unusedRent()) }}</strong>
                  <span class="bo-metric__note"><span>{{ i18n.t('rent.return.earlyBack') }}</span></span>
                </div>
                @if (relief().debtReduction > 0) {
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('rent.return.offDebt') }}</span>
                    <strong class="bo-metric__value mono">{{ i18n.money(relief().debtReduction) }}</strong>
                    <span class="bo-metric__note"><span>{{ i18n.t('rent.return.offDebtHint') }}</span></span>
                  </div>
                }
                @if (relief().paymentVoucher > 0) {
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('rent.return.paymentVoucher') }}</span>
                    <strong class="bo-metric__value mono">{{ i18n.money(relief().paymentVoucher) }}</strong>
                    <span class="bo-metric__note"><span>{{ i18n.t('rent.return.paymentVoucherHint') }}</span></span>
                  </div>
                }
              </div>
            </div>
          }

          @if (closesContract()) {
            <div class="bo-panel__body">
              <p class="notice ok" role="status">{{ i18n.t('rent.return.closesContract') }}</p>
            </div>
          }


        </section>
        }
        <!-- After both panels, so it stays on screen through the form and the charges alike. -->
        <div class="ui-actionbar ui-actionbar--sticky">
          <button type="button" class="btn-ghost" (click)="cancelled.emit()">{{ i18n.t('common.cancel') }}</button>
          <button type="button" class="btn-primary" [disabled]="busy() || !canSave()" (click)="save()">
            {{ i18n.t('rent.return.raise') }}
          </button>
        </div>
      } @else {
        <p class="bo-loading">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .usage-choice { margin-block: var(--sp-3); padding: var(--sp-3); border: 1px solid var(--border); border-radius: var(--radius-card); background: var(--surface-2); }
      .usage-choice legend { font-weight: 600; font-size: var(--fs-sm); color: var(--text); padding-inline: var(--sp-2); }
      .usage-choice label { display: inline-flex; align-items: center; gap: var(--sp-2); margin-inline-end: var(--sp-4); min-block-size: var(--control-h); cursor: pointer; }
      .usage-preview { padding-block: var(--sp-3); border-block-end: 1px solid var(--border); }
      .usage-preview p { margin-block: var(--sp-2); overflow-wrap: anywhere; }
      .form-panel {
        overflow: visible;
      }

      .late-choice {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3);
      }

      .late-choice__question {
        flex-basis: 100%;
        margin: 0;
        font-size: var(--fs-sm);
        font-weight: 600;
        color: var(--text);
      }

      /* Document Summary Grid */
      .return-doc-summary {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--radius-card);
        padding: 14px 18px;
        box-shadow: var(--shadow-sm);
        margin-block: var(--sp-3) var(--sp-4);
      }
      .return-doc-summary__head {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-block-end: var(--sp-3);
        border-block-end: 1px solid var(--border);
        padding-block-end: var(--sp-2);
      }
      .return-doc-summary__title {
        margin: 0;
        font-size: 16px;
        font-family: var(--font-display);
        font-weight: 600;
        color: var(--text);
      }
      .return-metrics-strip {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
        gap: var(--sp-2);
        margin-block-end: var(--sp-3);
      }
      .metric-box {
        background: var(--surface-2);
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
        padding: var(--sp-2) var(--sp-3);
        display: flex;
        flex-direction: column;
        align-items: center;
        text-align: center;
      }
      .metric-box--good { border-inline-start: 3.5px solid var(--ok); }
      .metric-box--repair { border-inline-start: 3.5px solid var(--warn); }
      .metric-box--damage { border-inline-start: 3.5px solid var(--danger); }
      .metric-box--missing { border-inline-start: 3.5px solid var(--accent); background: var(--surface-tint); }
      .metric-box--after { border-inline-start: 3.5px solid var(--muted); }

      .metric-label {
        font-size: var(--fs-micro, 11px);
        color: var(--muted);
        text-transform: uppercase;
        letter-spacing: 0.03em;
        margin-block-end: 2px;
      }
      .metric-num {
        font-size: var(--fs-lg);
        font-weight: 700;
        color: var(--text);
      }
      .metric-num--cleared {
        color: var(--ok);
      }
      .return-financial-strip {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-4);
        padding-block-start: var(--sp-3);
        border-block-start: 1px dashed var(--border);
        font-size: var(--fs-sm);
      }
      .fin-item {
        display: inline-flex;
        gap: var(--sp-2);
        align-items: baseline;
      }
      .fin-label { color: var(--muted); }
      .fin-value { font-weight: 700; color: var(--text); }

      /* Item Cards Stack */
      .return-items-stack {
        display: flex;
        flex-direction: column;
        gap: var(--sp-3);
        margin-block: var(--sp-3);
      }
      .return-item-card {
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow-sm);
        transition: border-color var(--t-fast);
        overflow: hidden;
      }
      .return-item-card.is-complete {
        border-color: var(--ok);
      }
      .return-item-card.is-invalid {
        border-color: var(--danger);
      }
      .return-card-header {
        padding: var(--sp-3) var(--sp-4);
        background: var(--surface);
        display: flex;
        flex-wrap: wrap;
        justify-content: space-between;
        align-items: center;
        gap: var(--sp-3);
        border-block-end: 1px solid var(--border);
      }
      .is-collapsed .return-card-header {
        border-block-end: none;
      }
      .return-card-info {
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .return-card-title-row {
        display: flex;
        align-items: center;
        flex-wrap: wrap;
        gap: var(--sp-2);
      }
      .item-sku {
        font-size: var(--fs-sm);
        font-weight: 700;
        padding: 1px 6px;
        background: var(--surface-2);
        border-radius: var(--radius-sm);
        border: 1px solid var(--border);
      }
      .item-name {
        margin: 0;
        font-size: var(--fs-md);
        font-weight: 600;
        color: var(--text);
      }
      .return-card-meta {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
        font-size: var(--fs-sm);
        color: var(--muted);
      }
      .out-badge strong {
        color: var(--text);
      }
      .meta-sep { color: var(--border); }

      .return-card-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
      }
      .reconcile-pill {
        font-size: var(--fs-sm);
        padding: 3px 10px;
        border-radius: var(--radius);
        border: 1px solid transparent;
        white-space: nowrap;
      }
      .reconcile-pill--none {
        background: var(--surface-2);
        color: var(--muted);
        border-color: var(--border);
      }
      .reconcile-pill--partial {
        background: var(--info-soft, #e3f2fd);
        color: var(--info, #0288d1);
      }
      .reconcile-pill--complete {
        background: var(--ok-soft, #e8f5e9);
        color: var(--ok, #2e7d32);
        font-weight: 600;
      }
      .reconcile-pill--exceeds {
        background: var(--danger-soft, #ffebee);
        color: var(--danger, #c62828);
        font-weight: 700;
      }

      .btn-convenience {
        background: transparent;
        border: 1px dashed var(--border);
        border-radius: var(--radius-sm);
        font-size: var(--fs-sm);
        color: var(--text);
        padding: 3px 10px;
        cursor: pointer;
        transition: all var(--t-fast);
      }
      .btn-convenience:hover {
        background: var(--surface-tint);
        border-style: solid;
        border-color: var(--accent);
      }
      .btn-toggle-card {
        background: var(--surface-2);
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
        font-size: var(--fs-sm);
        padding: 3px 10px;
        cursor: pointer;
      }
      .btn-toggle-card:hover {
        background: var(--border);
      }

      .return-card-collapsed-summary {
        padding: var(--sp-2) var(--sp-4) var(--sp-3);
        display: flex;
        justify-content: space-between;
        align-items: center;
        flex-wrap: wrap;
        gap: var(--sp-3);
        font-size: var(--fs-sm);
        background: var(--surface-2);
        border-block-start: 1px solid var(--border);
      }
      .summary-line {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-3);
        align-items: center;
      }
      .summary-cost {
        color: var(--text-2);
      }

      .return-card-body {
        padding: var(--sp-4);
        display: flex;
        flex-direction: column;
        gap: var(--sp-3);
      }
      .condition-control-group {
        display: grid;
        grid-template-columns: repeat(4, 1fr);
        gap: var(--sp-3);
        background: var(--surface-2);
        padding: var(--sp-3);
        border-radius: var(--radius);
        border: 1px solid var(--border);
      }
      @media (max-width: 900px) {
        .condition-control-group {
          grid-template-columns: repeat(2, 1fr);
        }
      }
      @media (max-width: 520px) {
        .condition-control-group {
          grid-template-columns: 1fr;
        }
      }
      .condition-col {
        display: flex;
        flex-direction: column;
        gap: var(--sp-1);
      }
      .condition-col label {
        font-size: var(--fs-sm);
        font-weight: 600;
        color: var(--text);
        display: flex;
        justify-content: space-between;
        align-items: center;
      }
      .condition-input {
        min-height: var(--control-h);
        font-size: var(--fs-md);
        text-align: center;
      }
      .col-missing {
        background: var(--surface);
        border: 1px dashed var(--border);
        border-radius: var(--radius-sm);
        padding: var(--sp-2);
      }
      .col-missing .missing-tag {
        font-size: var(--fs-micro, 10px);
        padding: 1px 5px;
        background: var(--accent);
        color: var(--on-accent);
        border-radius: var(--radius-sm);
      }

      .conditional-field-row {
        background: var(--surface-2);
        border-inline-start: 3px solid var(--accent);
        border-radius: var(--radius-sm);
        padding: var(--sp-3);
      }
      .line-error-banner {
        padding: var(--sp-2) var(--sp-3);
        background: var(--danger-soft, #ffebee);
        border: 1px solid var(--danger);
        border-radius: var(--radius-sm);
      }
      .validation-error-list {
        margin: var(--sp-2) 0 0 0;
        padding-inline-start: var(--sp-4);
      }
      .validation-error-list li {
        margin-block-end: var(--sp-1);
      }
      .btn-link {
        background: none;
        border: none;
        padding: 0;
        color: var(--danger);
        text-decoration: underline;
        cursor: pointer;
        text-align: start;
        font: inherit;
      }
    `,
  ],
})
export class RentalReturnNoteComponent implements OnInit {
  private readonly api = inject(RentalsApi);
  /** MS-23 (Phase M5a): the repair rates the recharge may be suggested from; never an error, and off unless the setting says so. */
  private readonly maintenance = inject(MaintenanceCustomerApi);
  readonly repairRates = signal<Record<string, number>>({});
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  readonly regional = inject(RegionalService);

  readonly contractId = input.required<string>();
  readonly branches = input<AdminBranch[]>([]);
  readonly products = input<AdminProduct[]>([]);
  readonly customers = input<AdminCustomer[]>([]);
  /** The company's rental settings, for the day cutoff hour. Absent reads as no cutoff. */
  readonly settings = input<RentalSettings | null>(null);

  /**
   * The note that was raised, not just the fact that one was: the shell shows a `done` sentence
   * after it and the Print link in that sentence needs the note's id (docs/07 §4.4). A listener
   * that only cares that the screen finished can go on ignoring the payload.
   */
  readonly done = output<ReturnNote>();
  readonly cancelled = output<void>();

  readonly docs = signal<ContractDocuments | null>(null);

  private readonly chrome = inject(PageChrome, { optional: true });
  /** Names this screen in the page header: its title, and the contract it belongs to as a step back. */
  private readonly named = effect(() => {
    const contract = this.docs()?.contract;
    this.chrome?.heading.set(this.i18n.t('rent.newReturn'));
    this.chrome?.description.set(contract ? contract.customerName || '—' : null);
    this.chrome?.trail.set(contract ? [{ label: contract.number, link: '/backoffice/rentals/contracts/' + contract.id }] : []);
  });

  readonly branchId = signal<string | null>(null);
  readonly returnedOn = signal<string>(today(this.regional.settings().timeZone));
  /** The hour the goods came back, in the company's zone — now, unless the clerk says otherwise. */
  readonly returnedTime = signal<string>(nowTime(this.regional.settings().timeZone));
  readonly note = signal('');
  readonly driverName = signal('');
  readonly driverPhone = signal('');
  readonly truckNumber = signal('');
  /** The customer carries the goods himself: no driver or truck is asked (owner, 2026-10-02). */
  readonly customerTransport = signal(false);
  /** The manager's decision on the return day; null follows the rule for the hour typed. */
  readonly decision = signal<boolean | null>(null);
  readonly reason = signal('');
  /** The answer to "charge the late days?" — nothing until the clerk gives one; asked only while late. */
  readonly lateAnswer = signal<'charge' | 'deduct' | 'waive' | null>(null);
  readonly lateReason = signal('');
  /** With "yes, less a deduction": in days or as an amount (the branch chooses), and how much. */
  readonly deductBy = signal<'days' | 'amount'>('days');
  readonly lateDeduction = signal<number | null>(null);
  readonly touched = new TouchedFields();

  private readonly returnedAtUtc = computed(() =>
    zonedTimeToUtcIso(
      this.returnedOn() || today(this.regional.settings().timeZone),
      this.returnedTime(),
      this.regional.settings().timeZone,
    ),
  );

  private readonly cutoff = computed(() => this.settings()?.dayCutoffHour ?? null);

  /** What the rule says for the hour typed: counted at or after the cutoff, not before, never without one. */
  readonly byDefault = computed(() =>
    countsReturnDayByDefault(localTime(this.returnedAtUtc(), this.regional.settings().timeZone), this.cutoff()),
  );

  readonly countReturnDay = computed(() => this.decision() ?? this.byDefault());
  readonly departsFromRule = computed(() => this.countReturnDay() !== this.byDefault());

  /** A method, not a computed: `TouchedFields` is a plain set, so a computed would never see a touch. */
  reasonInvalid(): boolean {
    return this.touched.missing(
      'reason',
      returnDayProblem(this.countReturnDay(), this.byDefault(), this.reason()) ? '' : 'ok',
    );
  }

  readonly transportGaps = computed(() =>
    this.customerTransport() ? [] : transportMissing(this.driverName(), this.driverPhone(), this.truckNumber()),
  );

  /** The rule's answer, in words, for the hour typed. */
  readonly ruleSentence = computed(() => {
    const cutoff = this.cutoff();
    if (cutoff === null) return this.i18n.t('rent.day.ruleNoCutoff');
    const h = `${String(cutoff).padStart(2, '0')}:00`;
    return this.i18n.t(this.byDefault() ? 'rent.day.ruleAfter' : 'rent.day.ruleBefore', { h, at: this.returnedTime() });
  });
  readonly draftLines = signal<Record<string, DraftLine>>({});
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** Only what is actually with the customer can come back. */
  readonly returnable = computed(() => (this.docs()?.contract.lines ?? []).filter((l) => l.stillOutQty > 0));

  /** The contract's branch by name: the only branch its goods come back to. */
  readonly contractBranchName = computed(() => {
    const id = this.docs()?.contract.branchId;
    const branch = this.branches().find((b) => b.id === id);
    return branch ? this.branchName(branch) : '—';
  });

  /**
   * A locked employee of another branch: the server would take the note at their branch and refuse
   * it, so the form says where the return is taken instead of sending it.
   */
  readonly otherBranch = computed(() => {
    const docs = this.docs();
    const locked = this.lock.lockedBranchId();
    return !!docs && locked !== null && locked !== docs.contract.branchId;
  });

  readonly preview = signal<ReturnPreview | null>(null);
  readonly previewPending = signal(false);
  readonly previewError = signal<string | null>(null);
  private previewSequence = 0;
  private previewKey = '';
  private readonly requestPreview = effect((cleanup) => {
    if (!this.docs() || !this.branchId()) return;
    const request = this.request();
    const id = this.contractId();
    const key = JSON.stringify(request);
    const sequence = ++this.previewSequence;
    untracked(() => { this.previewPending.set(request.lines.length > 0); this.previewError.set(null); });
    if (!request.lines.length) { untracked(() => this.preview.set(null)); return; }
    const timer = setTimeout(async () => {
      try {
        const result = await this.api.previewReturn(id, request);
        if (sequence !== this.previewSequence) return;
        this.previewKey = key;
        this.preview.set(result);
      } catch (error) {
        if (sequence !== this.previewSequence) return;
        this.preview.set(null);
        this.previewError.set(adminErrorMessage(error));
      } finally {
        if (sequence === this.previewSequence) this.previewPending.set(false);
      }
    }, 200);
    cleanup(() => { clearTimeout(timer); if (sequence === this.previewSequence) ++this.previewSequence; });
  });

  readonly late = computed(() => this.preview()?.late ?? null);
  usageRequired(lineId: string): boolean {
    return this.preview()?.rows.some(row => row.contractLineId === lineId && row.sameDayUsageRequired) ?? false;
  }
  moneyOrPending(value: number | null | undefined): string {
    return value == null ? '—' : this.i18n.money(value);
  }
  problemText(problem: string): string {
    if (problem === 'sameDayUsageRequired') return this.i18n.t('rent.usage.required');
    if (problem === 'sameDayUsageNotApplicable' || problem === 'invalidSameDayUsage') return this.i18n.t('rent.usage.invalid');
    return this.i18n.t('rent.problem.' + problem);
  }

  /** When the goods were due and when they came back, in words — and how long after, when they were. */
  readonly lateSentence = computed(() => {
    const late = this.late();
    if (!late) return '';
    const lang = this.i18n.lang();
    const cutoff = this.cutoff();
    const due = !late.dueOn
      ? ''
      : cutoff === null
        ? this.i18n.t('rent.late.dueBy', { day: this.isolate(formatRentalDay(late.dueOn, lang)) })
        : this.i18n.t('rent.late.dueAt', {
            day: this.isolate(formatRentalDay(late.dueOn, lang)),
            h: this.isolate(`${String(cutoff).padStart(2, '0')}:00`),
          });
    const values = { day: this.isolate(formatRentalDay(this.returnedOn(), lang)), at: this.isolate(this.returnedTime()) };
    const back =
      late.minutes > 0
        ? this.i18n.t('rent.late.backAfter', { ...values, d: this.isolate(this.lateBy(late.minutes)) })
        : this.i18n.t('rent.late.back', values);
    return due ? `${due} ${back}` : back;
  });

  /** The answer as the server takes it: null while the note is not late, or not answered yet. */
  private readonly chargeOverdue = computed<boolean | null>(() => {
    const answer = this.lateAnswer();
    return answer ? answer !== 'waive' : null;
  });

  /** The deduction as the server takes it, one kind or the other; an empty box is no number. */
  private readonly deduction = computed(() => {
    const value = this.lateDeduction();
    const n = this.lateAnswer() === 'deduct' && typeof value === 'number' && Number.isFinite(value) ? value : null;
    return { days: this.deductBy() === 'days' ? n : null, amount: this.deductBy() === 'amount' ? n : null };
  });

  /** Switching between days and amount starts the number over: 2 days is not 2 of the currency. */
  deductIn(by: 'days' | 'amount'): void {
    if (this.deductBy() === by) return;
    this.deductBy.set(by);
    this.lateDeduction.set(null);
  }

  /** What is wrong with the deduction, in words, or null. Missing is said once the box was left. */
  private readonly deductionProblem = computed(() => {
    const late = this.late();
    if (!late || this.lateAnswer() !== 'deduct') return null;
    const { days, amount } = this.deduction();
    if (days === null && amount === null) return this.i18n.t('rent.late.deductionRequired');
    const problem = lateDeductionProblem(true, days, amount, late.days, late.amount, this.regional.decimals());
    if (problem === 'lateDeductionDaysInvalid') return this.i18n.t('rent.late.deductionDaysInvalid', { n: late.days });
    if (problem) return this.i18n.t('rent.late.deductionAmountInvalid', { v: this.i18n.money(late.amount) });
    return null;
  });

  lateDeductionError(): string | null {
    const problem = this.deductionProblem();
    return this.touched.missing('lateDeduction', problem ? '' : 'ok') ? problem : null;
  }

  /** Charged and deducted, from the server's preview of this very deduction. */
  readonly deductionResult = computed(() => {
    const late = this.late();
    if (!late || this.lateAnswer() !== 'deduct' || this.deductionProblem()) return null;
    const charged = this.charges().filter(c => c.kind === 'Overdue').reduce((sum, c) => sum + c.amount, 0);
    return { charged, waived: Math.max(0, late.amount - charged) };
  });

  /** Methods, not computeds, for the same reason as `reasonInvalid`: a touch is not a signal. */
  lateUnanswered(): boolean {
    return this.touched.missing('late', this.late() && this.lateAnswer() === null ? '' : 'ok');
  }

  lateReasonInvalid(): boolean {
    return this.touched.missing('lateReason',
      lateWaiverProblem(this.chargeOverdue(), this.lateReason(), this.lateAnswer() === 'deduct') ? '' : 'ok');
  }

  /** "2 d 3 h", "5 h 20 min", "40 min": days and hours, or hours and minutes, never all three. */
  private lateBy(minutes: number): string {
    const days = Math.floor(minutes / 1440);
    const hours = Math.floor((minutes % 1440) / 60);
    const mins = minutes % 60;
    const parts =
      days > 0
        ? [this.i18n.t('rent.late.inDays', { n: days }), hours > 0 ? this.i18n.t('rent.late.inHours', { n: hours }) : '']
        : [hours > 0 ? this.i18n.t('rent.late.inHours', { n: hours }) : '', mins > 0 ? this.i18n.t('rent.late.inMinutes', { n: mins }) : ''];
    return parts.filter(Boolean).join(' ');
  }

  readonly charges = computed(() => this.preview()?.charges ?? []);
  readonly chargeTotal = computed(() => this.preview()?.chargeTotal ?? null);
  readonly unusedRent = computed(() => this.preview()?.unusedRent ?? null);
  readonly relief = computed(() => ({ debtReduction: this.preview()?.reliefDebtReduction ?? 0,
    paymentVoucher: this.preview()?.reliefPaymentVoucher ?? 0 }));
  readonly closesContract = computed(() => this.preview()?.isClosed ?? false);

  readonly collapsedCards = signal<Record<string, boolean>>({});
  readonly attemptedSave = signal(false);

  readonly totalOutBefore = computed(() => this.returnable().reduce((sum, l) => sum + l.stillOutQty, 0));
  readonly totalGood = computed(() => this.returnable().reduce((sum, l) => sum + (this.draft(l.id).qtyOk || 0), 0));
  readonly totalRepairable = computed(() => this.returnable().reduce((sum, l) => sum + (this.draft(l.id).qtyPartialDamage || 0), 0));
  readonly totalDamaged = computed(() => this.returnable().reduce((sum, l) => sum + (this.draft(l.id).qtyTotalDamage || 0), 0));
  readonly totalMissing = computed(() => this.returnable().reduce((sum, l) => sum + (this.draft(l.id).qtyMissing || 0), 0));
  readonly totalReturned = computed(() => this.totalGood() + this.totalRepairable() + this.totalDamaged() + this.totalMissing());
  readonly totalOutAfter = computed(() => Math.max(0, this.totalOutBefore() - this.totalReturned()));

  readonly totalMaintenanceValue = computed(() =>
    this.returnable().reduce((sum, l) => sum + (this.draft(l.id).maintenanceValue || 0), 0)
  );

  readonly totalCompensationValue = computed(() =>
    this.charges()
      .filter((c) => c.kind === 'Damage' || c.kind === 'Missing')
      .reduce((sum, c) => sum + c.amount, 0)
  );

  allocatedQty(lineId: string): number {
    const d = this.draft(lineId);
    return (d.qtyOk || 0) + (d.qtyPartialDamage || 0) + (d.qtyTotalDamage || 0) + (d.qtyMissing || 0);
  }

  reconciliationState(line: RentalContractLine): 'none' | 'partial' | 'complete' | 'exceeds' {
    const alloc = this.allocatedQty(line.id);
    if (alloc === 0) return 'none';
    if (alloc === line.stillOutQty) return 'complete';
    if (alloc < line.stillOutQty) return 'partial';
    return 'exceeds';
  }

  reconciliationText(line: RentalContractLine): string {
    const alloc = this.allocatedQty(line.id);
    const total = line.stillOutQty;
    if (alloc === 0) return `${alloc} / ${total}`;
    if (alloc === total) return this.i18n.t('rent.return.allocatedComplete', { total });
    if (alloc < total) {
      return this.i18n.t('rent.return.allocatedRemaining', { allocated: alloc, total, remaining: total - alloc });
    }
    return this.i18n.t('rent.return.allocatedExceeds', { allocated: alloc, total, excess: alloc - total });
  }

  setAllSound(line: RentalContractLine): void {
    this.patch(line.id, {
      qtyOk: line.stillOutQty,
      qtyPartialDamage: 0,
      maintenanceValue: 0,
      qtyTotalDamage: 0,
      qtyMissing: 0,
      reportedOn: '',
    });
    if (this.returnable().length > 1) {
      this.collapsedCards.update((s) => ({ ...s, [line.id]: true }));
    }
  }

  isCardCollapsed(lineId: string): boolean {
    const line = this.returnable().find((l) => l.id === lineId);
    if (!line) return false;
    if (this.hasCardError(line)) {
      return false;
    }
    return !!this.collapsedCards()[lineId];
  }

  toggleCard(lineId: string): void {
    const line = this.returnable().find((l) => l.id === lineId);
    if (line && this.hasCardError(line)) {
      this.collapsedCards.update((s) => ({ ...s, [lineId]: false }));
      return;
    }
    const next = !this.isCardCollapsed(lineId);
    this.collapsedCards.update((s) => ({ ...s, [lineId]: next }));
  }

  cardSummaryText(lineId: string): string {
    const d = this.draft(lineId);
    return this.i18n.t('rent.return.cardSummary', {
      returned: this.allocatedQty(lineId),
      good: d.qtyOk || 0,
      repairable: d.qtyPartialDamage || 0,
      damaged: d.qtyTotalDamage || 0,
      missing: d.qtyMissing || 0,
    });
  }

  hasCardError(line: RentalContractLine): boolean {
    return (
      this.problemOf(line) !== null ||
      this.allocatedQty(line.id) > line.stillOutQty ||
      (this.usageRequired(line.id) && !this.draft(line.id).sameDayUsage)
    );
  }

  cardErrorText(line: RentalContractLine): string | null {
    if (this.allocatedQty(line.id) > line.stillOutQty) {
      return this.i18n.t('rent.return.allocatedExceeds', {
        allocated: this.allocatedQty(line.id),
        total: line.stillOutQty,
        excess: this.allocatedQty(line.id) - line.stillOutQty,
      });
    }
    const prob = this.problemOf(line);
    if (prob) return this.i18n.t('rent.problem.' + prob);
    if (this.usageRequired(line.id) && !this.draft(line.id).sameDayUsage) {
      return this.i18n.t('rent.usage.required');
    }
    return null;
  }

  readonly linesWithErrors = computed(() =>
    this.returnable()
      .filter((l) => this.hasCardError(l))
      .map((l) => ({ line: l, errorText: this.cardErrorText(l)! }))
  );

  scrollToCard(lineId: string): void {
    this.collapsedCards.update((s) => ({ ...s, [lineId]: false }));
    setTimeout(() => {
      const el = document.getElementById('rc-card-' + lineId);
      if (el) {
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      }
    });
  }

  formatDate(day: string): string {
    return formatRentalDay(day, this.i18n.lang());
  }

  readonly canSave = computed(() => !!this.branchId() && !this.otherBranch() &&
    !this.previewPending() && !!this.preview()?.canConfirm &&
    !this.returnable().some(line => this.problemOf(line)) &&
    !this.returnable().some(line => this.usageRequired(line.id) && !this.draft(line.id).sameDayUsage));

  private request(): ReturnNoteRequest {
    const lines: ReturnNoteLineRequest[] = this.draftEntries(this.countReturnDay()).map(entry => ({
      contractLineId: entry.lineId, qtyOk: entry.qtyOk, qtyPartialDamage: entry.qtyPartialDamage,
      maintenanceValue: entry.maintenanceValue, qtyTotalDamage: entry.qtyTotalDamage, qtyMissing: entry.qtyMissing,
      reportedOnUtc: entry.reportedOn ? zonedTimeToUtcIso(entry.reportedOn, '12:00', this.regional.settings().timeZone) : null,
      sameDayUsage: this.draft(entry.lineId).sameDayUsage,
    }));
    return { branchId: this.branchId()!, lines, returnedAtUtc: this.returnedOn() ? this.returnedAtUtc() : null,
      note: this.note().trim() || null, driverName: this.driverName().trim(), driverPhone: this.driverPhone().trim(),
      truckNumber: this.truckNumber().trim(), customerTransport: this.customerTransport(),
      countReturnDay: this.departsFromRule() ? this.countReturnDay() : null,
      returnDayReason: this.departsFromRule() ? this.reason().trim() || null : null,
      chargeOverdue: this.chargeOverdue(),
      lateWaiverReason: this.lateAnswer() === 'waive' || this.lateAnswer() === 'deduct' ? this.lateReason().trim() || null : null,
      // Only a deduction carries a number, and only when it is the answer.
      ...(this.lateAnswer() === 'deduct' ? { lateDaysWaived: this.deduction().days, lateAmountWaived: this.deduction().amount } : {}) };
  }

  async ngOnInit(): Promise<void> {
    // Only a suggestion: read beside the contract, and silently nothing when the module or MS-23 is off or unreadable.
    void this.maintenance.repairSuggestions()
      .then((s) => this.repairRates.set(s.enabled ? Object.fromEntries(s.rates.map((r) => [r.productId, r.ratePerUnit])) : {}))
      .catch(() => this.repairRates.set({}));
    try {
      const docs = await this.api.readContract(this.contractId());
      this.docs.set(docs);
      // Goods come back to the contract's branch, whoever takes the note; moving them on is a
      // stock transfer afterwards.
      this.branchId.set(docs.contract.branchId);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  /** MS-23: the repairable units × the product's repair rate, rounded as money; null without a rate or units. */
  suggestedRepair(productId: string, qty: number): { qty: number; rate: number; amount: number } | null {
    const rate = this.repairRates()[productId];
    if (!rate || !(qty > 0)) return null;
    return { qty, rate, amount: roundTo(qty * rate, this.regional.decimals()) };
  }

  draft(lineId: string): DraftLine {
    return this.draftLines()[lineId] ?? BLANK;
  }

  patch(lineId: string, change: Partial<DraftLine>): void {
    if (Object.keys(change).some(key => key.startsWith('qty'))) change.sameDayUsage = null;
    this.draftLines.update((lines) => ({ ...lines, [lineId]: { ...this.draft(lineId), ...change } }));
  }

  setReturnDate(value: string): void {
    this.returnedOn.set(value);
    this.draftLines.update(lines => Object.fromEntries(Object.entries(lines).map(([id, line]) =>
      [id, { ...line, sameDayUsage: null }])));
  }

  /** A report dates the note only where nothing walked back in; a loss is declared on its own. */
  pureLoss(lineId: string): boolean {
    const draft = this.draft(lineId);
    return draft.qtyOk === 0 && draft.qtyPartialDamage === 0 && draft.qtyTotalDamage + draft.qtyMissing > 0;
  }

  private draftEntries(countsReturnDay: boolean): ReturnEntry[] {
    return this.returnable()
      .map((line) => ({ line, draft: this.draft(line.id) }))
      .filter(({ draft }) => this.settled(draft) > 0 || draft.maintenanceValue > 0)
      .map(({ line, draft }) => ({
        returnNoteId: DRAFT,
        lineId: line.id,
        returnedOn: this.returnedOn(),
        qtyOk: draft.qtyOk,
        qtyPartialDamage: draft.qtyPartialDamage,
        maintenanceValue: draft.maintenanceValue,
        qtyTotalDamage: draft.qtyTotalDamage,
        qtyMissing: draft.qtyMissing,
        reportedOn: draft.reportedOn || null,
        countsReturnDay,
      }));
  }

  /** A left-to-right number inside a sentence that may be Arabic: isolated so its digits keep their order. */
  isolate(text: string): string {
    return `\u2066${text}\u2069`;
  }

  transportInvalid(field: 'driverName' | 'driverPhone' | 'truckNumber'): boolean {
    return this.touched.missing(field, this.transportGaps().includes(field) ? '' : 'ok');
  }

  private settled(draft: DraftLine): number {
    return draft.qtyOk + draft.qtyPartialDamage + draft.qtyTotalDamage + draft.qtyMissing;
  }

  /**
   * The engine's own refusal, asked before the server is. A return bigger than what is out has to
   * stop here: once it is saved the FIFO match has nothing to charge it against.
   */
  problemOf(line: RentalContractLine): string | null {
    const draft = this.draft(line.id);
    if (this.settled(draft) === 0 && draft.maintenanceValue === 0) return null;
    return returnProblem(
      {
        returnNoteId: DRAFT,
        lineId: line.id,
        returnedOn: this.returnedOn(),
        qtyOk: draft.qtyOk,
        qtyPartialDamage: draft.qtyPartialDamage,
        maintenanceValue: draft.maintenanceValue,
        qtyTotalDamage: draft.qtyTotalDamage,
        qtyMissing: draft.qtyMissing,
        reportedOn: draft.reportedOn || null,
      },
      line.issuedQty,
      line.settledQty,
    );
  }

  name(line: RentalContractLine): string {
    return lineName(line, this.products(), this.i18n.lang());
  }

  nameOf(lineId: string): string {
    const line = this.docs()?.contract.lines.find((l) => l.id === lineId);
    return line ? this.name(line) : '—';
  }

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  /** The word carries the meaning; the tone only makes an overdue line findable in a long list. */
  chargeTone(kind: string): string {
    if (kind === 'Overdue') return 'bo-state--danger';
    if (kind === 'Rent') return 'bo-state--lead';
    return 'bo-state--warn';
  }

  async save(): Promise<void> {
    this.attemptedSave.set(true);
    // Said on the fields rather than by a greyed button: the server refuses the note without them.
    this.touched.touchAll('driverName', 'driverPhone', 'truckNumber', 'reason', 'late', 'lateReason', 'lateDeduction');
    if (this.transportGaps().length > 0) return;
    if (returnDayProblem(this.countReturnDay(), this.byDefault(), this.reason())) return;
    // Late goods are asked about, and the question is answered before the note goes.
    if (this.late() && this.lateAnswer() === null) return;
    if (lateWaiverProblem(this.chargeOverdue(), this.lateReason(), this.lateAnswer() === 'deduct')) return;
    // A deduction chosen and left empty would go as a plain yes: it is said on the field instead.
    if (this.deductionProblem()) return;
    if (!this.canSave()) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('rent.return.raise'))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const request = this.request();
      // A response for an older draft must never authorize a changed draft.
      if (JSON.stringify(request) !== this.previewKey) return;
      const note = await this.api.return(this.contractId(), request);
      this.done.emit(note);
    } catch (e) {
      const message = adminErrorMessage(e);
      this.error.set(message.includes('sameDayUsageRequired') ? this.i18n.t('rent.usage.required') : message);
    } finally {
      this.busy.set(false);
    }
  }
}
