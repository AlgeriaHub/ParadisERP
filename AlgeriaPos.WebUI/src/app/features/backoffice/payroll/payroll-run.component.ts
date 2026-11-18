import { Component, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError } from '../../../core/api.service';
import { moneyErrorMessage } from './wage-money-api.service';
import { I18nService } from '../../../core/i18n.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest, saveRequest } from '../../../ui/confirm-requests';
import { PrintService } from '../../print/print.service';
import { PayrollApi } from './payroll-api.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { DEFAULT_PAGE_SIZE } from '../../../ui/pager';
import {
  ElementGroup,
  LabelBook,
  PayrollRunDetail,
  PayrollRunLine,
  PayrollRunLinesPage,
  PayrollRunStatus,
  PayrollRunsApi,
  RUN_TONE,
  RunAction,
  availableActions,
  elementLabel,
  emptyLabelBook,
  groupElements,
  hasPostingPlaceholder,
  labelBook,
  labelIn,
  parseRefusal,
  periodLabel,
  postingPreview,
  stateKey,
  stillOwed,
} from './payroll-run-view';

/** A one-off figure being typed for one person in this run. */
interface AdjustmentDraft {
  key: string;
  amount: string;
  reason: string;
  inSocialBase: boolean;
  inTaxBase: boolean;
}

function emptyDraft(): AdjustmentDraft {
  return { key: '', amount: '', reason: '', inSocialBase: true, inTaxBase: true };
}

/**
 * One month's wages for one branch: **what it comes to, who it is wrong for, what approving it
 * will do to the books, and what may still be done to it.**
 *
 * <p>Four things are said in words rather than shown as a state of a button. The run's own state
 * is a word beside its tone. A run computed under unconfirmed statutory figures wears
 * <b>provisional</b> at the top and on every payslip it prints, for ever — the mark is frozen on
 * the document, not read live, so confirming the figures later does not quietly relabel February.
 * A compute that would be refused says which figure is missing and who may set it, before the
 * button is pressed. And each of the four lifecycle actions states what it is about to do — down
 * to the account codes the accrual will touch — and only then asks.</p>
 *
 * <p>Nothing here is a refusal the screen invented. The one-live-run rule, the transitions and
 * the refusal all live on the server; this screen shows the server's own sentence when it says
 * no, because a grey button teaches nobody why.</p>
 *
 * <p>The lines are paged server-side: a branch with nine hundred staff draws six tiles from the
 * header read and a hundred payslips at a time from a route of its own.</p>
 */
@Component({
  selector: 'app-payroll-run',
  standalone: true,
  imports: [FormsModule, ListFooterComponent],
  template: `
    <div class="bo-stack">
      <div class="bo-actions bo-actions--start">
        <button type="button" class="btn-ghost bo-small" (click)="back.emit()">{{ i18n.t('hr.run.back') }}</button>
      </div>

      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (warning()) {
        <p class="notice warn" role="status">{{ warning() }}</p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }
      @if (notFound()) {
        <p class="notice danger" role="alert">{{ i18n.t('hr.run.notFound') }}</p>
      }

      @if (detail(); as d) {
        <!-- ---------- what this document is ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">
              <span>{{ i18n.t('hr.run.title') }}</span>
              <span class="mono">{{ d.run.number }}</span>
              <span class="bo-soft">{{ d.run.branchName }}</span>
            </h2>
            <span class="bo-states">
              <span class="bo-state" [class]="tone(d.run.status)">{{ i18n.t(stateWord(d.run.status)) }}</span>
              <span class="bo-state">{{ period() }}</span>
              @if (d.run.isProvisional) {
                <span class="bo-state bo-state--warn">{{ i18n.t('hr.run.provisionalShort') }}</span>
              }
            </span>
            <p class="bo-meta">
              @if (d.run.computedAtUtc) {
                <span>{{ i18n.t('hr.run.meta.computed', { when: i18n.dateTime(d.run.computedAtUtc), who: d.run.lockedBy ?? '—' }) }}</span>
              }
              @if (d.run.approvedAtUtc) {
                <span>{{ i18n.t('hr.run.meta.approved', { when: i18n.dateTime(d.run.approvedAtUtc), who: d.run.approvedBy ?? '—' }) }}</span>
              }
              @if (d.run.paidAtUtc) {
                <span>{{ i18n.t('hr.run.meta.paid', { when: i18n.dateTime(d.run.paidAtUtc) }) }}</span>
              }
              @if (d.run.reversedAtUtc) {
                <span>{{ i18n.t('hr.run.meta.reversed', { when: i18n.dateTime(d.run.reversedAtUtc), who: d.run.reversedBy ?? '—' }) }}</span>
              }
              @if (d.run.reversalReason) {
                <span>{{ d.run.reversalReason }}</span>
              }
            </p>
          </div>
        </section>

        @if (d.run.isProvisional) {
          <div class="notice warn banner" role="status">
            <strong>{{ i18n.t('hr.run.provisional') }}</strong>
            <p>{{ i18n.t('hr.run.provisionalBody') }}</p>
          </div>
        }

        <!-- ---------- what stops it being computed at all ---------- -->
        @if (canCompute() && d.refusal; as refusal) {
          <div class="notice danger banner" role="alert">
            <strong>{{ i18n.t('hr.run.refusal.title') }}</strong>
            <p>{{ i18n.t(refusalKey(refusal.code)) }}</p>
            @if (refusal.missing.length) {
              <p>{{ i18n.t('hr.run.refusal.missing', { fields: refusal.missing.join(', ') }) }}</p>
            }
            <p>{{ i18n.t('hr.run.refusal.fixableBy', { perm: refusal.fixableBy }) }}</p>
          </div>
        }

        <!-- ---------- what it comes to ---------- -->
        <div class="bo-metrics">
          <div class="bo-metric bo-metric--lead">
            <span class="bo-metric__label">{{ i18n.t('hr.run.tile.net') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(d.run.net) }}</strong>
            <span class="bo-metric__note"><span>{{ i18n.t('hr.run.tile.netNote', { n: d.run.headCount }) }}</span></span>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('hr.run.tile.gross') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(d.run.gross) }}</strong>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('hr.run.tile.socialEmployee', { name: socialName() }) }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(d.run.socialEmployee) }}</strong>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('hr.run.tile.socialEmployer', { name: socialName() }) }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(d.run.socialEmployer) }}</strong>
            <span class="bo-metric__note"><span>{{ i18n.t('hr.run.tile.employerNote') }}</span></span>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('hr.run.tile.incomeTax', { name: taxName() }) }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(d.run.incomeTax) }}</strong>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('hr.run.tile.advances') }}</span>
            <strong class="bo-metric__value mono">{{ i18n.money(d.run.advancesRecovered) }}</strong>
          </div>
          @if (d.run.employerLevies > 0) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('hr.run.tile.employerLevies') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(d.run.employerLevies) }}</strong>
            </div>
          }
          @if (d.run.otherDeductions > 0) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('hr.run.tile.otherDeductions') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(d.run.otherDeductions) }}</strong>
            </div>
          }
          @if (d.run.paidTotal > 0) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('hr.run.tile.paid') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(d.run.paidTotal) }}</strong>
              <span class="bo-metric__note"><span>{{ i18n.t('hr.run.tile.stillOwed', { amount: i18n.money(d.run.net - d.run.paidTotal) }) }}</span></span>
            </div>
          }
        </div>

        <!-- ---------- who it is wrong for ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.run.warnings.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('hr.run.warnings.hint') }}</p>
          </div>
          <div class="bo-panel__body">
            @if (d.warnings.length) {
              <ul class="run-warnings">
                @for (w of d.warnings; track w.code) {
                  <li>
                    <span class="bo-state bo-state--warn">{{ i18n.t('hr.run.warnings.lines', { n: w.lines }) }}</span>
                    <span>{{ i18n.t(warningKey(w.code)) }}</span>
                    <button
                      type="button"
                      class="btn-ghost bo-small"
                      [class.is-on]="warningFilter() === w.code"
                      (click)="filterByWarning(w.code)"
                    >
                      {{ warningFilter() === w.code ? i18n.t('hr.run.warnings.clear') : i18n.t('hr.run.warnings.show') }}
                    </button>
                  </li>
                }
              </ul>
              @if (warningFilter()) {
                <p class="bo-hint">{{ i18n.t('hr.run.warnings.pageOnly') }}</p>
              }
            } @else {
              <p class="bo-hint">{{ i18n.t('hr.run.warnings.none') }}</p>
            }
          </div>
        </section>

        <!-- ---------- what approving will do to the books ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.run.posting.title') }}</h2>
            <p class="bo-panel__note">
              {{
                i18n.t(d.run.ledgerEntryId ? 'hr.run.posting.wrote' : 'hr.run.posting.will', {
                  branch: d.run.branchName,
                  debits: posting().debitCodes || '—',
                  credits: posting().creditCodes || '—',
                })
              }}
            </p>
          </div>
          <div class="bo-scroll">
            <table class="bo-data" [attr.aria-label]="i18n.t('hr.run.posting.title')">
              <thead>
                <tr>
                  <th>{{ i18n.t('hr.run.posting.account') }}</th>
                  <th class="num">{{ i18n.t('hr.run.posting.debit') }}</th>
                  <th class="num">{{ i18n.t('hr.run.posting.credit') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (line of d.posting; track $index) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('hr.run.posting.account')">{{ line.accountCode }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.run.posting.debit')">{{ line.debit ? i18n.money(line.debit) : '—' }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.run.posting.credit')">{{ line.credit ? i18n.money(line.credit) : '—' }}</td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="3" class="bo-empty"><strong>{{ i18n.t('hr.run.posting.none') }}</strong></td>
                  </tr>
                }
              </tbody>
              @if (d.posting.length) {
                <tfoot>
                  <tr>
                    <td>{{ i18n.t('common.total') }}</td>
                    <td class="num mono">{{ i18n.money(posting().debitTotal) }}</td>
                    <td class="num mono">{{ i18n.money(posting().creditTotal) }}</td>
                  </tr>
                </tfoot>
              }
            </table>
          </div>
          @if (placeholder()) {
            <div class="bo-panel__foot">
              <p class="bo-panel__note">{{ i18n.t('hr.run.posting.placeholder') }}</p>
            </div>
          }
        </section>

        <!-- ---------- the payslips ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.run.lines.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('hr.run.lines.hint') }}</p>
          </div>

          <div class="bo-panel__body">
            <div class="bo-fields">
              <div class="bo-field bo-field--wide">
                <label for="run-q">{{ i18n.t('hr.run.lines.search') }}</label>
                <input id="run-q" type="search" [ngModel]="q()" (ngModelChange)="search($event)" [attr.placeholder]="i18n.t('hr.run.lines.searchHint')" />
              </div>
            </div>
          </div>

          <div class="bo-scroll bo-scroll--tall">
            <table class="bo-data" [attr.aria-label]="i18n.t('hr.run.lines.title')">
              <thead>
                <tr>
                  <th>{{ i18n.t('hr.run.lines.employee') }}</th>
                  <th>{{ i18n.t('hr.run.lines.basis') }}</th>
                  <th class="num">{{ i18n.t('hr.run.lines.days') }}</th>
                  <th class="num">{{ i18n.t('hr.run.tile.gross') }}</th>
                  <th class="num">{{ socialName() }}</th>
                  <th class="num">{{ taxName() }}</th>
                  <th class="num">{{ i18n.t('hr.run.lines.advance') }}</th>
                  <th class="num">{{ i18n.t('hr.run.tile.net') }}</th>
                  <th class="bo-cell-actions">{{ i18n.t('common.actions') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (line of visibleLines(); track line.id) {
                  <tr [class.is-muted]="line.net === 0">
                    <td [attr.data-label]="i18n.t('hr.run.lines.employee')">
                      <span class="mono" dir="ltr">{{ line.employeeNumber }}</span>
                      <span>{{ line.employeeName }}</span>
                      @if (line.warnings?.length) {
                        <span class="bo-state bo-state--warn">{{ i18n.t('hr.run.lines.warned', { n: line.warnings!.length }) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('hr.run.lines.basis')">
                      <span>{{ i18n.t(basisWord(line.payBasis)) }}</span>
                      <span class="bo-state">{{ i18n.t(sourceWord(line.attendanceSource)) }}</span>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.run.lines.days')">{{ days(line) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.run.tile.gross')">{{ i18n.money(line.gross) }}</td>
                    <td class="num mono" [attr.data-label]="socialName()">{{ i18n.money(line.socialEmployee) }}</td>
                    <td class="num mono" [attr.data-label]="taxName()">{{ i18n.money(line.incomeTax) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.run.lines.advance')">
                      {{ i18n.money(line.advanceRecovered) }}
                      @if (line.paidByVoucher) {
                        <span class="ui-sub">{{ i18n.t('hr.run.lines.paidByVoucher', { value: i18n.money(line.paidByVoucher) }) }}</span>
                      }
                    </td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.run.tile.net')">{{ i18n.money(line.net) }}</td>
                    <td class="bo-cell-actions" [attr.data-label]="i18n.t('common.actions')">
                      <button type="button" class="btn-ghost bo-small" (click)="expand(line)">
                        {{ expanded() === line.id ? i18n.t('hr.run.lines.hide') : i18n.t('hr.run.lines.detail') }}
                      </button>
                      <button type="button" class="btn-ghost bo-small" (click)="printOne(line)">{{ i18n.t('hr.run.payslip') }}</button>
                    </td>
                  </tr>
                  @if (expanded() === line.id) {
                    <tr>
                      <td colspan="9" class="run-detail">
                        <!-- what the figure is made of -->
                        <table class="bo-data">
                          <thead>
                            <tr>
                              <th>{{ i18n.t('hr.run.element.line') }}</th>
                              <th class="num">{{ i18n.t('hr.run.element.qty') }}</th>
                              <th class="num">{{ i18n.t('hr.run.element.rate') }}</th>
                              <th class="num">{{ i18n.t('hr.run.element.amount') }}</th>
                            </tr>
                          </thead>
                          @for (group of elements(line); track group.kind) {
                            <tbody>
                              @for (el of group.elements; track el.key + '-' + el.sortOrder) {
                                <tr>
                                  <td [attr.data-label]="i18n.t('hr.run.element.line')">
                                    <span>{{ label(el) }}</span>
                                    <span class="bo-state">{{ i18n.t(kindWord(el.kind)) }}</span>
                                  </td>
                                  <td class="num mono" [attr.data-label]="i18n.t('hr.run.element.qty')">{{ el.qty ?? '—' }}</td>
                                  <td class="num mono" [attr.data-label]="i18n.t('hr.run.element.rate')">{{ el.rate ? i18n.money(el.rate) : '—' }}</td>
                                  <td class="num mono" [attr.data-label]="i18n.t('hr.run.element.amount')">{{ i18n.money(el.amount) }}</td>
                                </tr>
                              }
                            </tbody>
                          } @empty {
                            <tbody>
                              <tr>
                                <td colspan="4" class="bo-empty"><strong>{{ i18n.t('hr.run.element.none') }}</strong></td>
                              </tr>
                            </tbody>
                          }
                          <tfoot>
                            <tr>
                              <td colspan="3">{{ i18n.t('hr.run.lines.withheld') }}</td>
                              <td class="num mono">{{ i18n.money(line.socialEmployee + line.incomeTax + line.advanceRecovered + (line.paidByVoucher ?? 0) + line.otherDeductions) }}</td>
                            </tr>
                            <tr class="bo-cell-strong">
                              <td colspan="3">{{ i18n.t('hr.run.tile.net') }}</td>
                              <td class="num mono">{{ i18n.money(line.net) }}</td>
                            </tr>
                            @if (line.paidAmount > 0) {
                              <tr>
                                <td colspan="3">{{ i18n.t('hr.run.lines.stillOwed') }}</td>
                                <td class="num mono">{{ i18n.money(owed(line)) }}</td>
                              </tr>
                            }
                          </tfoot>
                        </table>

                        <!-- what is wrong with it, in words -->
                        @if (line.warnings?.length) {
                          <ul class="run-warnings">
                            @for (code of line.warnings; track code) {
                              <li><span class="bo-state bo-state--warn">{{ i18n.t('hr.run.warnings.one') }}</span><span>{{ i18n.t(warningKey(code)) }}</span></li>
                            }
                          </ul>
                        }

                        <!-- what somebody typed for this month only -->
                        <div class="run-adjust">
                          <h3 class="bo-group__title">{{ i18n.t('hr.run.adj.title') }}</h3>
                          @if (adjustmentsFor(line).length) {
                            <ul class="run-warnings">
                              @for (a of adjustmentsFor(line); track a.id) {
                                <li>
                                  <span class="mono">{{ a.key }}</span>
                                  <span class="mono">{{ i18n.money(a.amount) }}</span>
                                  <span>{{ a.reason }}</span>
                                </li>
                              }
                            </ul>
                          } @else {
                            <p class="bo-hint">{{ i18n.t('hr.run.adj.none') }}</p>
                          }

                          @if (canAdjust()) {
                            <div class="bo-fields">
                              <div class="bo-field">
                                <label [attr.for]="'adj-key-' + line.id">{{ i18n.t('hr.run.adj.key') }}</label>
                                <input [id]="'adj-key-' + line.id" type="text" class="mono" [ngModel]="draft().key" (ngModelChange)="setDraft('key', $event)" />
                              </div>
                              <div class="bo-field">
                                <label [attr.for]="'adj-amount-' + line.id">{{ i18n.t('hr.run.adj.amount') }}</label>
                                <input [id]="'adj-amount-' + line.id" type="number" step="0.01" class="num mono" [ngModel]="draft().amount" (ngModelChange)="setDraft('amount', $event)" />
                                <span class="bo-hint">{{ i18n.t('hr.run.adj.zeroRemoves') }}</span>
                              </div>
                              <div class="bo-field bo-field--wide">
                                <label [attr.for]="'adj-reason-' + line.id">{{ i18n.t('hr.run.adj.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                                <input [id]="'adj-reason-' + line.id" type="text" [ngModel]="draft().reason" (ngModelChange)="setDraft('reason', $event)" />
                              </div>
                            </div>
                            <div class="bo-check">
                              <input [id]="'adj-social-' + line.id" type="checkbox" [ngModel]="draft().inSocialBase" (ngModelChange)="setDraft('inSocialBase', $event)" />
                              <label [attr.for]="'adj-social-' + line.id">{{ i18n.t('hr.field.inSocialBase') }}</label>
                            </div>
                            <div class="bo-check">
                              <input [id]="'adj-tax-' + line.id" type="checkbox" [ngModel]="draft().inTaxBase" (ngModelChange)="setDraft('inTaxBase', $event)" />
                              <label [attr.for]="'adj-tax-' + line.id">{{ i18n.t('hr.field.inTaxBase') }}</label>
                            </div>
                            <div class="bo-actions">
                              <span class="bo-hint">{{ i18n.t('hr.run.adj.recompute') }}</span>
                              <button type="button" class="btn-ghost" [disabled]="!canSaveDraft() || busy()" (click)="saveAdjustment(line)">
                                {{ i18n.t('hr.run.adj.save') }}
                              </button>
                            </div>
                          } @else {
                            <p class="bo-hint">{{ i18n.t('hr.run.adj.locked') }}</p>
                          }
                        </div>
                      </td>
                    </tr>
                  }
                } @empty {
                  <tr>
                    <td colspan="9" class="bo-empty">
                      @if (busy()) {
                        <strong>{{ i18n.t('common.loading') }}</strong>
                        <span class="bo-loading__rows" aria-hidden="true"></span>
                      } @else {
                        <strong>{{ i18n.t('hr.run.lines.none') }}</strong>
                        <span>{{ i18n.t('hr.run.lines.noneHint') }}</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          @if (page(); as p) {
            @if (p.total) {
              <!-- The server pages the payslips (skip/take); the footer turns its pages and sets its size. -->
              <div class="bo-panel__body">
                <app-list-footer
                  [total]="p.total"
                  [pageIndex]="linePageIndex()"
                  [pageSize]="lineSize()"
                  [loading]="busy() && !p.lines.length"
                  (pageChange)="goToLines($event)"
                  (pageSizeChange)="setLineSize($event)"
                />
              </div>
            }
          }
        </section>

        <!-- ---------- what may still be done ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.run.actions.title') }}</h2>
            @if (!actions().length) {
              <p class="bo-panel__note">{{ i18n.t(finalWord()) }}</p>
            }
          </div>

          @if (pending(); as action) {
            <div class="bo-panel__body">
              <div class="notice warn banner" role="status">
                <strong>{{ i18n.t(actionWord(action)) }}</strong>
                <p>{{ actionSentence(action) }}</p>
                @if (action === 'reverse') {
                  <div class="bo-field bo-field--wide">
                    <label for="run-reason">{{ i18n.t('hr.run.reverse.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                    <input id="run-reason" type="text" [ngModel]="reason()" (ngModelChange)="reason.set($event)" />
                  </div>
                }
              </div>
              <div class="bo-actions">
                <button type="button" class="btn-ghost" (click)="pending.set(null)">{{ i18n.t('common.cancel') }}</button>
                <button type="button" class="btn-primary" [disabled]="busy() || (action === 'reverse' && !reason().trim())" (click)="run(action)">
                  {{ i18n.t('common.confirm') }}
                </button>
              </div>
            </div>
          }

          <div class="bo-panel__foot">
            @if (d.run.headCount > 0) {
              <button type="button" class="btn-ghost" (click)="printAll()">{{ i18n.t('hr.run.printPayslips') }}</button>
            }
            @if (needsRecompute()) {
              <p class="notice warn" role="status">{{ i18n.t('hr.run.actions.recomputeFirst') }}</p>
            }
            @for (action of actions(); track action) {
              <button
                type="button"
                [class]="action === 'approve' ? 'btn-primary' : 'btn-ghost'"
                [disabled]="busy() || pending() === action || blocked(action)"
                (click)="ask(action)"
              >
                {{ i18n.t(actionWord(action)) }}
              </button>
            }
          </div>
        </section>
      } @else if (busy()) {
        <p class="bo-loading">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* A title holding a word, a number and a name needs the gap between the pair, which under
         RTL is not what a plain margin gives. Same rule as the invoice screen's. */
      .bo-panel__title > * + * {
        margin-inline-start: 0.45rem;
      }

      /* Warnings read as sentences, one per row, the count leading. */
      .run-warnings {
        list-style: none;
        margin: 0;
        padding: 0;
        display: grid;
        gap: 0.35rem;
      }

      .run-warnings li {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.45rem;
      }

      /* The expanded payslip is a panel inside a row, recessed so the eye keeps the table. */
      .run-detail {
        background: var(--surface-2);
      }

      .run-adjust {
        margin-block-start: 0.75rem;
      }
    `,
  ],
})
export class PayrollRunComponent implements OnInit {
  readonly i18n = inject(I18nService);
  private readonly api = inject(PayrollRunsApi);
  private readonly settingsApi = inject(PayrollApi);
  private readonly printer = inject(PrintService);
  private readonly confirm = inject(ConfirmService);

  readonly runId = input.required<string>();
  readonly back = output<void>();

  readonly detail = signal<PayrollRunDetail | null>(null);
  readonly page = signal<PayrollRunLinesPage | null>(null);
  /**
   * The wording an element prints under, from the settings. A signal rather than a field: it
   * arrives after the first read, and a label that never redraws is a payslip full of keys.
   */
  readonly book = signal<LabelBook>(emptyLabelBook());
  readonly skip = signal(0);
  /** Payslips a page asks the server for: ten to begin with, 20, 50 or 100 at the user's choice (owner, 2026-11-17). */
  readonly lineSize = signal(DEFAULT_PAGE_SIZE);
  readonly linePageIndex = computed(() => Math.floor(this.skip() / this.lineSize()));
  readonly q = signal('');
  readonly expanded = signal<string | null>(null);
  readonly warningFilter = signal<string | null>(null);
  readonly pending = signal<RunAction | null>(null);
  readonly reason = signal('');
  readonly draft = signal<AdjustmentDraft>(emptyDraft());
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly warning = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly notFound = signal(false);

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Reopened on another run — from the list, or a deep link later — the state of the old one
    // must not survive into it: a stale page of lines under a new header is a wrong payslip.
    effect(() => {
      const id = this.runId();
      if (!id) return;
      this.skip.set(0);
      this.q.set('');
      this.expanded.set(null);
      this.warningFilter.set(null);
      this.pending.set(null);
      void this.load();
    });
  }

  async ngOnInit(): Promise<void> {
    // The labels an element prints under are the company's own words; without them a payslip
    // reads as a column of keys. A settings read that fails is not a reason to hide the run.
    const view = await this.settingsApi.settings().catch(() => null);
    this.book.set(labelBook(view?.settings ?? null));
  }

  // ---------- reading ----------

  async load(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.notFound.set(false);
    try {
      const [detail, page] = await Promise.all([this.api.read(this.runId()), this.api.lines(this.runId(), this.skip(), this.lineSize(), this.q())]);
      this.detail.set(detail);
      this.page.set(page);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) this.notFound.set(true);
      else this.error.set(moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)));
    } finally {
      this.busy.set(false);
    }
  }

  private async loadLines(): Promise<void> {
    this.busy.set(true);
    try {
      this.page.set(await this.api.lines(this.runId(), this.skip(), this.lineSize(), this.q()));
    } catch (e) {
      this.error.set(moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)));
    } finally {
      this.busy.set(false);
    }
  }

  search(value: string): void {
    this.q.set(value);
    this.skip.set(0);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    // A branch of nine hundred would otherwise send a request per keystroke.
    this.searchTimer = setTimeout(() => void this.loadLines(), 250);
  }

  /** A page of payslips from the footer: the server is asked for it, as the old next/previous did. */
  goToLines(index: number): void {
    const next = Math.max(0, index * this.lineSize());
    if (next === this.skip()) return;
    this.skip.set(next);
    this.expanded.set(null);
    void this.loadLines();
  }

  /** Another page size starts again at the first page. */
  setLineSize(size: number): void {
    if (size === this.lineSize()) return;
    this.lineSize.set(size);
    this.skip.set(0);
    this.expanded.set(null);
    void this.loadLines();
  }

  // ---------- the lifecycle ----------

  readonly actions = computed<RunAction[]>(() => {
    const run = this.detail()?.run;
    return run ? availableActions(run.status) : [];
  });

  /**
   * The refusal is carried on every read, including a run computed months ago under settings
   * that have since been emptied. It is worth a red panel only where it would actually bite:
   * on a run somebody can still press Compute on.
   */
  readonly canCompute = computed(() => this.actions().includes('compute'));

  /**
   * True while an adjustment has been typed that the computed figures do not carry. Approve is
   * disabled on it and says why: approving posts around the adjustment and then strands it — the
   * run leaves Locked, so it can no longer be edited, and this screen would go on listing a bonus
   * the payslip denies. The server refuses the same case; the button simply says so first.
   */
  readonly needsRecompute = computed(() => this.detail()?.recomputeNeeded === true);

  blocked(action: RunAction): boolean {
    return action === 'approve' && this.needsRecompute();
  }

  /** A run with nothing left to do says which of the two ends it reached. */
  finalWord(): string {
    const status = this.detail()?.run.status;
    return status === 'Paid' ? 'hr.run.actions.paidFinal' : status === 'Reversed' ? 'hr.run.actions.reversedFinal' : 'hr.run.actions.none';
  }

  ask(action: RunAction): void {
    this.done.set(null);
    this.warning.set(null);
    this.reason.set('');
    this.pending.set(action);
  }

  /** What the action is about to do, said before it is done — including to the books. */
  actionSentence(action: RunAction): string {
    const detail = this.detail();
    if (!detail) return '';
    switch (action) {
      case 'compute':
        return this.i18n.t(detail.run.status === 'Locked' ? 'hr.run.actions.recomputeWill' : 'hr.run.actions.computeWill');
      case 'unlock':
        return this.i18n.t('hr.run.actions.unlockWill');
      case 'approve':
        return this.i18n.t('hr.run.actions.approveWill', {
          branch: detail.run.branchName,
          debits: this.posting().debitCodes || '—',
          credits: this.posting().creditCodes || '—',
        });
      case 'reverse':
        return this.i18n.t('hr.run.actions.reverseWill', { number: detail.run.number });
    }
  }

  async run(action: RunAction): Promise<void> {
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t(this.actionWord(action)), undefined, action === 'reverse')))) return;
    this.busy.set(true);
    this.error.set(null);
    this.warning.set(null);
    this.done.set(null);
    try {
      const id = this.runId();
      const envelope =
        action === 'compute'
          ? await this.api.compute(id)
          : action === 'unlock'
            ? await this.api.unlock(id)
            : action === 'approve'
              ? await this.api.approve(id)
              : await this.api.reverse(id, this.reason().trim());
      // The warning travels beside the document, never instead of it.
      if (envelope.warning) this.warning.set(this.sayWarning(envelope.warning));
      this.done.set(this.i18n.t('hr.run.actions.done', { action: this.i18n.t(this.actionWord(action)) }));
      this.pending.set(null);
      this.skip.set(0);
      await this.load();
    } catch (e) {
      // A compute refusal names the missing figure and who may set it; anything else is the
      // server's own sentence, shown as it stands.
      const refusal = parseRefusal(e);
      this.error.set(
        refusal
          ? `${this.i18n.t(this.refusalKey(refusal.code))} ${this.i18n.t('hr.run.refusal.fixableBy', { perm: refusal.fixableBy })}`
          : moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)),
      );
    } finally {
      this.busy.set(false);
    }
  }

  // ---------- adjustments ----------

  /** Only while the run can still be recomputed: an approved run is corrected in the next one. */
  readonly canAdjust = computed(() => {
    const status = this.detail()?.run.status;
    return status === 'Draft' || status === 'Locked';
  });

  readonly canSaveDraft = computed(() => {
    const draft = this.draft();
    return !!draft.key.trim() && !!draft.reason.trim() && Number.isFinite(Number(draft.amount));
  });

  setDraft<K extends keyof AdjustmentDraft>(field: K, value: AdjustmentDraft[K]): void {
    // A generic computed key widens the literal, so the shape is restated rather than inferred.
    this.draft.set({ ...this.draft(), [field]: value } as AdjustmentDraft);
  }

  async saveAdjustment(line: PayrollRunLine): Promise<void> {
    const draft = this.draft();
    const amount = Number(draft.amount);
    if (!this.canSaveDraft()) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n)))) return;
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      const envelope = await this.api.upsertAdjustment(this.runId(), line.employeeId, {
        employeeId: line.employeeId,
        key: draft.key.trim(),
        amount,
        reason: draft.reason.trim(),
        inSocialBase: draft.inSocialBase,
        inTaxBase: draft.inTaxBase,
      });
      if (envelope.warning) this.warning.set(this.sayWarning(envelope.warning));
      // An amount of zero removes it; the sentence says which of the two happened.
      this.done.set(this.i18n.t(envelope.adjustment ? 'hr.run.adj.saved' : 'hr.run.adj.removed'));
      this.draft.set(emptyDraft());
      await this.load();
    } catch (e) {
      this.error.set(moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)));
    } finally {
      this.busy.set(false);
    }
  }

  adjustmentsFor(line: PayrollRunLine) {
    return (this.detail()?.run.adjustments ?? []).filter((a) => a.employeeId === line.employeeId);
  }

  // ---------- printing ----------

  printAll(): void {
    this.printer.open('payslip', this.runId(), { lang: this.i18n.lang() });
  }

  printOne(line: PayrollRunLine): void {
    this.printer.open('payslip', this.runId(), { lang: this.i18n.lang(), line: line.id });
  }

  // ---------- shaping ----------

  readonly posting = computed(() => postingPreview(this.detail()?.posting));
  readonly placeholder = computed(() => hasPostingPlaceholder(this.detail()?.posting));

  /** The contribution's and the tax's own names, in the reader's language. */
  readonly socialName = computed(() => labelIn(this.book().social, this.i18n.lang()) || this.i18n.t('hr.run.tile.socialFallback'));
  readonly taxName = computed(() => labelIn(this.book().incomeTax, this.i18n.lang()) || this.i18n.t('hr.run.tile.taxFallback'));

  readonly visibleLines = computed<PayrollRunLine[]>(() => {
    const lines = this.page()?.lines ?? [];
    const code = this.warningFilter();
    return code ? lines.filter((l) => (l.warnings ?? []).includes(code)) : lines;
  });

  filterByWarning(code: string): void {
    this.warningFilter.set(this.warningFilter() === code ? null : code);
  }

  expand(line: PayrollRunLine): void {
    const open = this.expanded() === line.id;
    this.expanded.set(open ? null : line.id);
    if (!open) this.draft.set(emptyDraft());
  }

  elements(line: PayrollRunLine): ElementGroup[] {
    return groupElements(line.elements);
  }

  label(element: { key: string; kind: ElementGroup['kind'] }): string {
    return elementLabel(element, this.book(), this.i18n.lang(), (key) => this.i18n.t(key));
  }

  /** Days present, paid leave and unpaid absence as one cell: 26 / 2 / 1. */
  days(line: PayrollRunLine): string {
    return `${line.daysPresent} / ${line.paidLeaveDays} / ${line.unpaidAbsenceDays}`;
  }

  owed(line: PayrollRunLine): number {
    return stillOwed(line);
  }

  period(): string {
    const run = this.detail()?.run;
    return run ? periodLabel({ year: run.periodYear, month: run.periodMonth }, this.i18n.lang()) : '';
  }

  stateWord(status: string): string {
    return stateKey('hr.run.status', status);
  }

  basisWord(basis: string): string {
    return stateKey('hr.basis', basis);
  }

  sourceWord(source: string): string {
    return stateKey('hr.attendance.source', source);
  }

  kindWord(kind: string): string {
    return stateKey('hr.element.kind', kind);
  }

  warningKey(code: string): string {
    return stateKey('hr.warning', code);
  }

  /**
   * A warning that came back beside a document. The lines carry codes, which the dictionaries
   * say in the reader's language; the run and the adjustment envelopes still carry an English
   * sentence, and a sentence nobody can translate is better shown than swallowed.
   */
  private sayWarning(warning: string): string {
    const key = this.warningKey(warning);
    const said = this.i18n.t(key);
    return said === key ? warning : said;
  }

  refusalKey(code: string): string {
    return stateKey('hr.refusal', code);
  }

  actionWord(action: RunAction): string {
    return stateKey('hr.run.actions', action);
  }

  tone(status: PayrollRunStatus): string {
    return RUN_TONE[status] ?? '';
  }
}
