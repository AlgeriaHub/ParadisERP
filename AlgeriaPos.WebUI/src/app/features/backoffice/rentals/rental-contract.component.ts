import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { mayDo } from '../../../core/landing';
import { Component, OnDestroy, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminBranch, AdminProduct, adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { rentalAmount } from '../../../core/rentals';
import { formatDay, localizedName } from '../backoffice-format';
import { ContractDocuments, RentalAttachment, RentalContractLine, RentalsApi } from './rentals-api.service';
import { PrintService } from '../../print/print.service';
import { ContractWording, StoreDocumentsApi } from '../../print/store-documents-api.service';
import { identityMissing, signatoryState, signedByKey } from './rental-signatory';
import {
  OpenBatch,
  STATE_TONE,
  seesCancelled,
  contractState,
  dueState,
  formatRentalDay,
  lineName,
  openBatches,
} from './rental-view';
import { ConfirmService } from '../../../ui/confirm.service';
import { deleteRequest } from '../../../ui/confirm-requests';
import { MENU } from '../../../ui/menu.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';

type Tab = 'overview' | 'items' | 'documents' | 'attachments';
const TABS: Tab[] = ['overview', 'items', 'documents', 'attachments'];
type Action = 'issue' | 'return' | 'extend' | 'edit';

type Documents = 'issues' | 'returns' | 'extensions';

/**
 * One contract, opened to answer one question: **what do they owe me.**
 *
 * <p>So the money leads, and it leads broken into the four things it can be — hire, overdue,
 * compensation and the part of a prepaid period that has to be given back — because "12 400"
 * with no shape to it is the figure a customer argues with.</p>
 *
 * <p>Underneath it is the other half of the answer: **when each batch is due**. The server has
 * no route for that — it prices documents that exist, and a batch still on site has produced
 * none — so it is worked out here with `core/rentals.ts`, the engine's mirror, from the issue
 * notes, the line durations and the extensions signed over them. Per batch, never per contract:
 * ten frames can be due tomorrow and forty in three weeks on this same page.</p>
 */
@Component({
  selector: 'app-rental-contract',
  standalone: true,
  imports: [RouterLink, FormsModule, ...PAGE_CHROME, ...MENU, StatusComponent, TableStateComponent],
  template: `

    <!-- The page header carries what this contract allows next (REDESIGN_PLAN.md §13): one primary
         action — goods out while some are still to go, else a return while some are out — Print,
         and everything else in the ⋯ menu. Only what the contract already allows is offered. -->
    <ng-template appPageActions>
      @if (docs(); as d) {
        @if (primaryAction(); as a) {
          <button type="button" class="btn-primary" (click)="runAction(a, d.contract.id)">{{ i18n.t(actionLabel(a)) }}</button>
        }
        <!-- A cancelled contract is not handed to anyone: a print would not say it was called off. -->
        @if (d.contract.status !== 'Cancelled') {
          <button type="button" class="btn-ghost" (click)="printContract(d.contract.id)">{{ i18n.t('common.print') }}</button>
        }
        @if (moreActions().length) {
          <app-menu triggerClass="btn-ghost" [label]="i18n.t('ui.menu.more')">
            <span menuTrigger>{{ i18n.t('rent.page.more') }} <span aria-hidden="true">⋯</span></span>
            @for (a of moreActions(); track a) {
              <button type="button" appMenuItem (click)="runAction(a, d.contract.id)">{{ i18n.t(actionLabel(a)) }}</button>
            }
          </app-menu>
        }
      }
    </ng-template>

    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      @if (docs(); as d) {
        <!-- What the contract is, in one line: its state in words, whether it can still change,
             the day it was signed, its branch, and the contracts it extends or was extended by. -->
        <div class="status-line">
          <app-status [tone]="stateTone()">{{ i18n.t('rent.state.' + state()) }}</app-status>
          @if (d.contract.status !== 'Cancelled') {
            <span class="ui-tag" [class.ui-tag--accent]="!d.contract.isLocked">
              {{ i18n.t(d.contract.isLocked ? 'rent.locked' : 'rent.editable') }}
            </span>
          }
          <span>{{ i18n.t('rent.signedOn') }} <span class="mono">{{ day(d.contract.signedAtUtc) }}</span></span>
          <span>{{ branchName(d.contract.branchId) }}</span>
          <!-- Answer 8: an extension is its own contract; each names the other. -->
          @if (d.contract.extendsContractId && d.contract.extendsContractNumber) {
            <span>
              {{ i18n.t('rent.extendNew.extends') }}
              <button type="button" class="ui-link mono" (click)="openContract.emit(d.contract.extendsContractId!)">{{ d.contract.extendsContractNumber }}</button>
            </span>
          }
          @if (d.contract.extendedByContractId && d.contract.extendedByContractNumber) {
            <span>
              {{ i18n.t('rent.extendNew.extendedBy') }}
              <button type="button" class="ui-link mono" (click)="openContract.emit(d.contract.extendedByContractId!)">{{ d.contract.extendedByContractNumber }}</button>
            </span>
          }
        </div>

        @if (d.contract.status === 'Cancelled') {
          <p class="notice warn" role="status">
            {{ i18n.t('rent.cancel.done', { by: d.contract.cancelledBy || '—', on: day(d.contract.cancelledAtUtc ?? null), reason: d.contract.cancelReason || '—' }) }}
          </p>
        } @else if (!d.contract.isLocked) {
          <p class="notice warn">
            {{ i18n.t('rent.draftWarning') }}
            @if (signatory() === 'missing') {
              {{ i18n.t('rent.sign.blankDraft') }}
            }
          </p>
        }
        @if (d.contract.closedShortReason) {
          <p class="notice warn">
            {{ i18n.t('rent.pay.closedShort', { by: d.contract.closedShortBy || '—', reason: d.contract.closedShortReason }) }}
          </p>
        }

        <!-- Peers of one contract (§11): the address keeps the open one (?tab=). -->
        <div class="ui-tabs" role="tablist" [attr.aria-label]="d.contract.number" (keydown)="onTabKey($event)">
          @for (t of tabs; track t) {
            <button
              type="button"
              class="ui-tab"
              role="tab"
              [id]="'rc-tab-' + t"
              [attr.aria-selected]="openTab() === t"
              [attr.aria-controls]="'rc-panel-' + t"
              [attr.tabindex]="openTab() === t ? 0 : -1"
              (click)="tabChange.emit(t)"
            >
              {{ i18n.t('rent.page.tab.' + t) }}
              @if (tabCount(t); as n) {
                <span class="ui-tab__count">{{ n }}</span>
              }
            </button>
          }
        </div>

        <div class="bo-stack" role="tabpanel" [id]="'rc-panel-' + openTab()" [attr.aria-labelledby]="'rc-tab-' + openTab()">
          @switch (openTab()) {
            @case ('overview') {
              <!-- ---------- what do they owe me ---------- -->
              <div class="ui-metrics">
                <!-- What the contract is worth over its full period, the server's figure (owner,
                     2026-10-06): before anything goes out, "charged so far" alone read as zero. -->
                <div class="ui-metric">
                  <span class="ui-metric__label">{{ i18n.t('rent.form.contractValue') }}</span>
                  <strong class="ui-metric__value mono">{{ i18n.money(d.contract.rentalTotal ?? 0) }}</strong>
                </div>
                <div class="ui-metric ui-metric--lead">
                  <span class="ui-metric__label">{{ i18n.t('rent.chargedSoFar') }}</span>
                  <strong class="ui-metric__value mono">{{ i18n.money(d.settlement.chargeTotal) }}</strong>
                  <span class="ui-metric__note">{{ i18n.t('rent.chargedSoFarNote') }}</span>
                </div>
                <!-- A breakdown of nothing is three zeroes competing with the figure that matters. -->
                @if (d.settlement.chargeTotal > 0) {
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('rent.kind.Rent') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(d.settlement.rent) }}</strong>
                  </div>
                  <div class="ui-metric" [class.ui-metric--warn]="d.settlement.overdue > 0">
                    <span class="ui-metric__label">{{ i18n.t('rent.kind.Overdue') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(d.settlement.overdue) }}</strong>
                    <span class="ui-metric__note">{{ i18n.t('rent.overdueTileNote') }}</span>
                  </div>
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('rent.compensation') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(compensation()) }}</strong>
                    <span class="ui-metric__note">{{ i18n.t('rent.compensationNote') }}</span>
                  </div>
                }
                @if (d.settlement.unusedRent > 0) {
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('rent.toGiveBack') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(d.settlement.unusedRent) }}</strong>
                    <span class="ui-metric__note">{{ i18n.t('rent.toGiveBackNote') }}</span>
                    <!-- The refund of the unused period, for the whole contract (owner, 2026-10-06). -->
                    <a class="ui-link" [routerLink]="['/backoffice', 'finance', 'vouchers', 'new']" [queryParams]="{ contract: d.contract.id }">
                      {{ i18n.t('rent.refundUnused') }}
                    </a>
                  </div>
                }
                <!-- The deposit stands outside the money above: held on the customer's behalf, never
                     netted. Hidden at zero; its label turns into "to return" the day the contract
                     closes — stated, never blocking, no money moved until a payment voucher exists. -->
                @if (depositHeld() > 0) {
                  <div class="ui-metric" [class.ui-metric--warn]="depositToReturn() > 0">
                    <span class="ui-metric__label">{{ i18n.t(depositToReturn() > 0 ? 'rent.depositToReturn' : 'rent.depositHeld') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(depositHeld()) }}</strong>
                    <span class="ui-metric__note">{{ i18n.t(depositToReturn() > 0 ? 'rent.depositToReturnNote' : 'rent.depositHeldNote') }}</span>
                  </div>
                }
              </div>

              <!-- What the contract is worth and what has come in against it (answers 15, 22): it
                   closes when the two meet, a cheque counting once it is collected. -->
              @if (d.contract.isLocked && d.contract.owed !== undefined) {
                <section class="ui-surface">
                  <div class="ui-surface__head">
                    <h2>{{ i18n.t('rent.pay.title') }}</h2>
                  </div>
                  <div class="ui-surface__body pay">
                    <span class="mono figure">{{ i18n.t('rent.pay.paidOf', { paid: money(d.contract.paid ?? 0), owed: money(d.contract.owed ?? 0) }) }}</span>
                    @if (state() === 'unpaid') {
                      <app-status tone="warning">{{ i18n.t('rent.pay.remaining', { n: money(remaining()) }) }}</app-status>
                    }
                  </div>
                  @if (isAdmin() && state() === 'unpaid') {
                    <div class="ui-surface__body close-short bo-form">
                      <div class="bo-field">
                        <label for="rc-close-reason">{{ i18n.t('rent.pay.closeShortReason') }}</label>
                        <input id="rc-close-reason" maxlength="400" [ngModel]="closeReason()" (ngModelChange)="closeReason.set($event)" />
                        <span class="bo-hint">{{ i18n.t('rent.pay.closeShortHint') }}</span>
                      </div>
                      <div class="ui-actionbar">
                        <button type="button" class="btn-ghost" [disabled]="!closeReason().trim() || closing()" (click)="closeShort(d.contract.id)">
                          {{ i18n.t('rent.pay.closeShort') }}
                        </button>
                      </div>
                    </div>
                  }
                </section>
              }

              <!-- Called off before its exit note (owner, 2026-10-06): never once anything has
                   gone out, and the reason goes on it. Afterwards only the Admin sees it. -->
              @if (state() === 'draft') {
                <section class="ui-surface" aria-labelledby="rc-cancel-title">
                  <div class="ui-surface__head">
                    <h2 id="rc-cancel-title">{{ i18n.t('rent.cancel.title') }}</h2>
                  </div>
                  <div class="ui-surface__body close-short bo-form">
                    <div class="bo-field">
                      <label for="rc-cancel-reason">{{ i18n.t('rent.cancel.reason') }}</label>
                      <input id="rc-cancel-reason" maxlength="300" [ngModel]="cancelReason()" (ngModelChange)="cancelReason.set($event)" />
                      <span class="bo-hint">{{ i18n.t('rent.cancel.hint') }}</span>
                    </div>
                    <div class="ui-actionbar">
                      <button type="button" class="btn-ghost" [disabled]="!cancelReason().trim() || cancelling()" (click)="cancelContract(d.contract.id, d.contract.number)">
                        {{ i18n.t('rent.cancel.action') }}
                      </button>
                    </div>
                  </div>
                </section>
              }

              <!-- ---------- when it is due back ---------- -->
              <section class="ui-surface">
                <div class="ui-surface__head">
                  <h2>{{ i18n.t('rent.dueBack') }}</h2>
                  <p>{{ i18n.t('rent.dueBackHint') }}</p>
                </div>
                @if (batches().length) {
                  <div class="ui-scroll">
                    <table class="ui-table ui-table--cards">
                      <thead>
                        <tr>
                          <th>{{ i18n.t('rent.item') }}</th>
                          <th>{{ i18n.t('rent.wentOutOn') }}</th>
                          <th class="num">{{ i18n.t('rent.stillOut') }}</th>
                          <th class="num">{{ i18n.t('rent.entitledDays') }}</th>
                          <th>{{ i18n.t('rent.dueOn') }}</th>
                          <th>{{ i18n.t('common.state') }}</th>
                          <th class="num">{{ i18n.t('rent.overdueRunningAt') }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (batch of batches(); track batch.issueNoteId + batch.lineId) {
                          <tr>
                            <td [attr.data-label]="i18n.t('rent.item')">{{ name(batch.lineId) }}</td>
                            <td [attr.data-label]="i18n.t('rent.wentOutOn')">
                              <span class="mono">{{ rentalDay(batch.issuedOn) }}</span>
                              <span class="ui-sub mono">{{ noteNumber(batch.issueNoteId) }}</span>
                            </td>
                            <td class="num mono" [attr.data-label]="i18n.t('rent.stillOut')">{{ batch.qty }}</td>
                            <td class="num mono" [attr.data-label]="i18n.t('rent.entitledDays')">{{ batch.entitledDays }}</td>
                            <td class="mono" [attr.data-label]="i18n.t('rent.dueOn')">{{ rentalDay(batch.dueOn) }}</td>
                            <td [attr.data-label]="i18n.t('common.state')">
                              @switch (due(batch)) {
                                @case ('overdue') {
                                  <app-status tone="critical">{{ i18n.t('rent.due.overdue', { n: batch.daysOverdue }) }}</app-status>
                                }
                                @case ('dueToday') {
                                  <app-status tone="warning">{{ i18n.t('rent.due.today') }}</app-status>
                                }
                                @default {
                                  <app-status tone="success">{{ i18n.t('rent.due.in', { n: -batch.daysOverdue }) }}</app-status>
                                }
                              }
                            </td>
                            <td class="num mono" [attr.data-label]="i18n.t('rent.overdueRunningAt')">
                              {{ due(batch) === 'overdue' ? i18n.money(overdueSoFar(batch)) : '—' }}
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                } @else {
                  <app-table-state kind="empty" [message]="i18n.t('rent.nothingOut') + ' ' + i18n.t('rent.nothingOutHint')" />
                }
              </section>

              <!-- ---------- the rest of the paper: the site (answer 9), who signed and on what (10) ---------- -->
              <section class="ui-surface">
                <div class="ui-surface__head">
                  <h2>{{ i18n.t('rent.page.facts') }}</h2>
                </div>
                <dl class="ui-surface__body ui-facts">
                  <div>
                    <dt>{{ i18n.t('rent.site.label') }}</dt>
                    <dd>
                      @if (d.contract.siteName) {
                        {{ d.contract.siteName }}
                        @if (d.contract.siteAddress) {
                          <span class="ui-sub">{{ d.contract.siteAddress }}</span>
                        }
                      } @else {
                        <span class="ui-sub">{{ i18n.t('rent.site.notRecorded') }}</span>
                      }
                    </dd>
                  </div>
                  <!-- The frozen snapshot, never the customer's current representative: the paper
                       keeps the name of whoever signed it. -->
                  <div>
                    <dt>{{ i18n.t('rent.page.signatory') }}</dt>
                    <dd>
                      @switch (signatory()) {
                        @case ('signed') {
                          {{ i18n.t(signedByKey(d.contract), { name: d.contract.signatoryName! }) }}
                          @if (d.contract.signatoryIdNumber) {
                            <span class="ui-sub mono">{{ i18n.t('rent.sign.id', { n: isolate(d.contract.signatoryIdNumber) }) }}</span>
                          } @else {
                            <span class="ui-sub">{{ i18n.t('rent.sign.idMissing') }}</span>
                          }
                        }
                        @case ('predates') {
                          <span class="ui-sub">{{ i18n.t('rent.sign.predates') }}</span>
                        }
                        @default {
                          <app-status tone="warning">{{ i18n.t('rent.sign.missing') }}</app-status>
                        }
                      }
                    </dd>
                  </div>
                  @if (signatory() === 'signed') {
                    <div>
                      <dt>{{ i18n.t('rent.id.type') }}</dt>
                      <dd>
                        @if (d.contract.signatoryIdType) {
                          {{ i18n.t('rent.id.kind.' + d.contract.signatoryIdType) }}
                        }
                        @if (d.contract.signatoryIdIssuer) {
                          <span class="ui-sub">{{ i18n.t('rent.id.issuedBy', { by: d.contract.signatoryIdIssuer }) }}</span>
                        }
                        @if (d.contract.signatoryIdIssuedOn) {
                          <span class="ui-sub">{{ i18n.t('rent.id.issuedOnDay', { day: rentalDay(d.contract.signatoryIdIssuedOn) }) }}</span>
                        }
                        @if (d.contract.signatoryBirthDate) {
                          <span class="ui-sub">{{ i18n.t('rent.id.bornOn', { day: rentalDay(d.contract.signatoryBirthDate) }) }}</span>
                        }
                        @if (identityGaps().length > 0) {
                          <app-status tone="warning">{{ i18n.t('rent.id.incomplete', { fields: identityGapWords() }) }}</app-status>
                        }
                      </dd>
                    </div>
                  }
                  @if (d.contract.notes) {
                    <div>
                      <dt>{{ i18n.t('rent.notes') }}</dt>
                      <dd>{{ d.contract.notes }}</dd>
                    </div>
                  }
                  <!-- The named wording it prints (PinSay #302); the standard one is not worth a line. -->
                  @if (wordingName(); as w) {
                    <div>
                      <dt>{{ i18n.t('rent.page.wording') }}</dt>
                      <dd>{{ w }}</dd>
                    </div>
                  }
                  <!-- Whether its bills carry VAT (owner, 2026-11-02), said in words on every contract. -->
                  <div>
                    <dt>{{ i18n.t('rent.form.taxMode') }}</dt>
                    <dd>{{ i18n.t(d.contract.taxMode === 'Untaxed' ? 'inv.taxMode.Untaxed' : 'inv.taxMode.Taxed') }}</dd>
                  </div>
                </dl>
              </section>
            }

            @case ('items') {
              <!-- ---------- the lines ---------- -->
              <section class="ui-surface">
                <div class="ui-surface__head">
                  <h2>{{ i18n.t('rent.lines') }}</h2>
                  <p>{{ i18n.t('rent.linesHint') }}</p>
                </div>
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('rent.item') }}</th>
                        <th class="num">{{ i18n.t('rent.contracted') }}</th>
                        <th class="num">{{ i18n.t('rent.issued') }}</th>
                        <th class="num">{{ i18n.t('rent.leftToIssue') }}</th>
                        <th class="num">{{ i18n.t('rent.stillOut') }}</th>
                        <th class="num">{{ i18n.t('rent.durationDays') }}</th>
                        <th class="num">{{ i18n.t('rent.hirePrice') }}</th>
                        <th class="num">{{ i18n.t('rent.form.fullPeriod') }}</th>
                        <th class="num">{{ i18n.t('rent.overduePrice') }}</th>
                        <th class="num">{{ i18n.t('rent.damageTotal') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (line of d.contract.lines; track line.id) {
                        <tr>
                          <td [attr.data-label]="i18n.t('rent.item')">
                            {{ name(line.id) }}
                            <span class="ui-sub mono">{{ line.sku }}</span>
                            @if (line.parentLineId) {
                              <span class="ui-tag">{{ i18n.t('rent.accessoryOf', { of: name(line.parentLineId) }) }}</span>
                            }
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('rent.contracted')">{{ line.qty }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('rent.issued')">{{ line.issuedQty }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('rent.leftToIssue')">{{ line.leftToIssueQty }}</td>
                          <td class="num mono figure" [attr.data-label]="i18n.t('rent.stillOut')">{{ line.stillOutQty }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('rent.durationDays')">
                            {{ line.parentLineId ? parentDuration(line) : line.durationDays }}
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('rent.hirePrice')">{{ i18n.money(line.rentUnitPrice) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('rent.form.fullPeriod')">{{ i18n.money(line.rentalTotal ?? 0) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('rent.overduePrice')">{{ i18n.money(line.overdueUnitPrice) }}</td>
                          <!-- Every piece of the line at its price per piece (owner, 2026-10-06): what the goods are worth. -->
                          <td class="num mono" [attr.data-label]="i18n.t('rent.damageTotal')">{{ i18n.money(line.qty * line.damageUnitPrice) }}</td>
                        </tr>
                      }
                    </tbody>
                    <tfoot>
                      <tr><th colspan="9">{{ i18n.t('rent.form.contractValue') }}</th><td class="num mono">{{ i18n.money(d.contract.rentalTotal ?? 0) }}</td></tr>
                    </tfoot>
                  </table>
                </div>
              </section>
            }

            @case ('documents') {
              <!-- ---------- the chain of documents ---------- -->
              <section class="ui-surface">
                <div class="ui-surface__head">
                  <h2>{{ i18n.t('rent.documents') }}</h2>
                  <p>{{ i18n.t('rent.documentsHint') }}</p>
                </div>
                <div class="ui-surface__body">
                  <nav class="bo-chips" [attr.aria-label]="i18n.t('rent.documents')">
                    @for (choice of documentKinds; track choice) {
                      <button
                        type="button"
                        [class.is-on]="documents() === choice"
                        [attr.aria-pressed]="documents() === choice"
                        (click)="documents.set(choice)"
                      >
                        {{ i18n.t('rent.docs.' + choice) }}
                      </button>
                    }
                  </nav>
                </div>

                @if (documents() === 'issues') {
                  @if (d.issueNotes.length) {
                    <div class="ui-scroll">
                      <table class="ui-table ui-table--cards">
                        <thead>
                          <tr>
                            <th>{{ i18n.t('rent.issueNote') }}</th>
                            <th>{{ i18n.t('rent.wentOutOn') }}</th>
                            <th>{{ i18n.t('rent.branch') }}</th>
                            <th class="num">{{ i18n.t('register.qty') }}</th>
                            <th class="num">{{ i18n.t('rent.periodInvoiced') }}</th>
                            <th>{{ i18n.t('rent.raisedBy') }}</th>
                            <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (note of d.issueNotes; track note.id) {
                            <tr>
                              <td class="mono" [attr.data-label]="i18n.t('rent.issueNote')">
                                {{ note.number }}
                                @if (note.isCarryOver) {
                                  <span class="ui-tag">{{ i18n.t('rent.carry.in') }}</span>
                                }
                              </td>
                              <td class="mono" [attr.data-label]="i18n.t('rent.wentOutOn')">{{ day(note.issuedAtUtc) }}</td>
                              <td [attr.data-label]="i18n.t('rent.branch')">{{ branchName(note.branchId) }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('register.qty')">{{ qtyOf(note.lines) }}</td>
                              <td class="num mono figure" [attr.data-label]="i18n.t('rent.periodInvoiced')">{{ i18n.money(note.periodRent) }}</td>
                              <td [attr.data-label]="i18n.t('rent.raisedBy')">{{ note.userName || '—' }}</td>
                              <td class="ui-cell-menu">
                                <!-- A carry-over is a record, not a paper: no truck took anything anywhere. -->
                                @if (!note.isCarryOver) {
                                  <button type="button" class="btn-ghost" (click)="printIssueNote(note.id, d.contract.id)">{{ i18n.t('common.print') }}</button>
                                }
                              </td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  } @else {
                    <app-table-state kind="empty" [message]="i18n.t('rent.noIssueNotes')" />
                  }
                }

                @if (documents() === 'returns') {
                  @if (d.returnNotes.length) {
                    <div class="ui-scroll">
                      <table class="ui-table ui-table--cards">
                        <thead>
                          <tr>
                            <th>{{ i18n.t('rent.returnNote') }}</th>
                            <th>{{ i18n.t('rent.returnedOn') }}</th>
                            <th>{{ i18n.t('rent.branch') }}</th>
                            <th class="num">{{ i18n.t('rent.settled') }}</th>
                            <th class="num">{{ i18n.t('rent.thisNoteCharges') }}</th>
                            <th class="num">{{ i18n.t('rent.toGiveBack') }}</th>
                            <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (note of d.returnNotes; track note.id) {
                            <tr>
                              <td class="mono" [attr.data-label]="i18n.t('rent.returnNote')">
                                {{ note.number }}
                                @if (note.isCarryOver) {
                                  <span class="ui-tag">{{ i18n.t('rent.carry.out') }}</span>
                                }
                              </td>
                              <td class="mono" [attr.data-label]="i18n.t('rent.returnedOn')">{{ day(note.returnedAtUtc) }}</td>
                              <td [attr.data-label]="i18n.t('rent.branch')">{{ branchName(note.branchId) }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('rent.settled')">{{ settledOf(note.lines) }}</td>
                              <td class="num mono figure" [attr.data-label]="i18n.t('rent.thisNoteCharges')">{{ i18n.money(note.chargeTotal) }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('rent.toGiveBack')">{{ i18n.money(note.unusedRent) }}</td>
                              <td class="ui-cell-menu">
                                @if (!note.isCarryOver) {
                                  <button type="button" class="btn-ghost" (click)="printReturnNote(note.id, d.contract.id)">{{ i18n.t('common.print') }}</button>
                                }
                                @if (note.reliefPaymentVoucher > 0) {
                                  <a class="btn-ghost" [routerLink]="['/backoffice', 'finance', 'vouchers', 'new']" [queryParams]="{ returnNote: note.id }">
                                    {{ i18n.t('rent.return.raiseVoucher') }}
                                  </a>
                                }
                              </td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  } @else {
                    <app-table-state kind="empty" [message]="i18n.t('rent.noReturnNotes')" />
                  }
                }

                @if (documents() === 'extensions') {
                  @if (d.extensions.length) {
                    <div class="ui-scroll">
                      <table class="ui-table ui-table--cards">
                        <thead>
                          <tr>
                            <th>{{ i18n.t('rent.extension') }}</th>
                            <th>{{ i18n.t('rent.raisedOn') }}</th>
                            <th>{{ i18n.t('rent.item') }}</th>
                            <th class="num">{{ i18n.t('rent.extraDays') }}</th>
                            <th class="num">{{ i18n.t('rent.atPrice') }}</th>
                            <th>{{ i18n.t('common.state') }}</th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (extension of d.extensions; track extension.id) {
                            <tr>
                              <td class="mono" [attr.data-label]="i18n.t('rent.extension')">{{ extension.number }}</td>
                              <td class="mono" [attr.data-label]="i18n.t('rent.raisedOn')">{{ day(extension.raisedOnUtc) }}</td>
                              <td [attr.data-label]="i18n.t('rent.item')">
                                {{ extension.contractLineId ? name(extension.contractLineId) : i18n.t('rent.everyLine') }}
                              </td>
                              <td class="num mono" [attr.data-label]="i18n.t('rent.extraDays')">{{ extension.extraDays }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('rent.atPrice')">{{ i18n.money(extension.unitPrice) }}</td>
                              <td [attr.data-label]="i18n.t('common.state')">
                                @if (extension.isRetroactive) {
                                  <app-status tone="warning">{{ i18n.t('rent.retroactive') }}</app-status>
                                  <span class="ui-sub">{{ extension.approvedByName || '—' }}</span>
                                } @else {
                                  <app-status tone="success">{{ i18n.t('rent.signedInTime') }}</app-status>
                                }
                                @if (extension.reason) {
                                  <span class="ui-sub">{{ extension.reason }}</span>
                                }
                              </td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  } @else {
                    <app-table-state kind="empty" [message]="i18n.t('rent.noExtensions') + ' ' + i18n.t('rent.noExtensionsHint')" />
                  }
                }
              </section>
            }

            @case ('attachments') {
              <!-- ---------- the identity scan (answer 10a) ---------- -->
              <section class="ui-surface">
                <div class="ui-surface__head">
                  <h2>{{ i18n.t('rent.scan.title') }}</h2>
                  <p>{{ i18n.t(d.contract.isLocked ? 'rent.scan.lockedHint' : 'rent.scan.hint') }}</p>
                </div>
                @if (!canSeeScans()) {
                  <!-- The contract carries no copies for a user without customers.documents.view
                       (owner, 2026-10-25): say so, rather than "none kept". -->
                  <app-table-state kind="empty" [message]="i18n.t('rent.scan.restricted')" />
                } @else if (attachments().length) {
                  <div class="ui-scroll">
                    <table class="ui-table ui-table--cards">
                      <thead>
                        <tr>
                          <th>{{ i18n.t('rent.scan.file') }}</th>
                          <th class="num">{{ i18n.t('rent.scan.size') }}</th>
                          <th>{{ i18n.t('rent.scan.uploadedOn') }}</th>
                          <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (file of attachments(); track file.id) {
                          <tr>
                            <td [attr.data-label]="i18n.t('rent.scan.file')">{{ file.fileName }}</td>
                            <td class="num mono" [attr.data-label]="i18n.t('rent.scan.size')">{{ sizeOf(file) }}</td>
                            <td class="mono" [attr.data-label]="i18n.t('rent.scan.uploadedOn')">{{ day(file.uploadedAtUtc) }}</td>
                            <td class="ui-cell-menu">
                              <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + file.fileName">
                                <span menuTrigger aria-hidden="true">⋯</span>
                                <button type="button" appMenuItem [disabled]="scanBusy()" (click)="viewScan(file)">{{ i18n.t('rent.scan.view') }}</button>
                                @if (!d.contract.isLocked) {
                                  <hr appMenuSeparator />
                                  <button type="button" appMenuItem class="is-danger" [disabled]="scanBusy()" (click)="removeScan(file)">{{ i18n.t('rent.scan.remove') }}</button>
                                }
                              </app-menu>
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                } @else {
                  <app-table-state kind="empty" [message]="i18n.t('rent.scan.none') + ' ' + i18n.t('rent.scan.noneHint')" />
                }
                <div class="ui-surface__body bo-form scan-add">
                  <div class="bo-field">
                    <label for="rc-scan-choose">{{ i18n.t('rent.scan.add') }}</label>
                    <!-- The browser's own file control speaks the browser's language, not the app's. -->
                    <div class="bo-file">
                      <button type="button" id="rc-scan-choose" class="btn-ghost" [disabled]="scanBusy()" (click)="rcScan.click()">
                        {{ i18n.t('common.chooseFile') }}
                      </button>
                      <span class="bo-file__name">{{ rcScan.files?.[0]?.name || i18n.t('common.noFile') }}</span>
                      <input
                        #rcScan
                        id="rc-scan"
                        type="file"
                        hidden
                        accept="image/jpeg,image/png,image/webp,application/pdf"
                        [disabled]="scanBusy()"
                        (change)="uploadScan($event)"
                      />
                    </div>
                    <p class="bo-hint">{{ i18n.t('rent.scan.limits') }}</p>
                    @if (scanError()) {
                      <span class="bo-error" role="alert">{{ scanError() }}</span>
                    }
                  </div>
                </div>
              </section>
            }
          }
        </div>
      } @else if (!error()) {
        <app-table-state kind="loading" />
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .status-line {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2) var(--sp-4);
        padding: 12px 18px;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--radius-card);
        font-size: 13px;
        color: var(--muted);
      }
      .figure {
        font-weight: 700;
      }
      .pay {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3);
        font-size: var(--fs-md);
      }
      .close-short {
        display: grid;
        gap: var(--sp-3);
        border-block-start: 1px solid var(--border);
      }
      .scan-add {
        border-block-start: 1px solid var(--border);
      }
    `,
  ],
})
export class RentalContractComponent implements OnInit, OnDestroy {
  private readonly api = inject(RentalsApi);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  /** Closing a contract short of payment is rentals.close's (the Admin's by role); read from the role on a session from before keys. */
  readonly isAdmin = computed(() => mayDo(this.auth.currentUser(), 'rentals.close'));
  readonly seesCancelled = computed(() => seesCancelled(this.auth.currentUser()));
  readonly closeReason = signal('');
  readonly closing = signal(false);
  readonly cancelReason = signal('');
  readonly cancelling = signal(false);
  readonly remaining = computed(() => {
    const contract = this.docs()?.contract;
    return contract ? Math.max(0, (contract.owed ?? 0) - (contract.paid ?? 0)) : 0;
  });

  money(value: number): string {
    return this.i18n.money(value);
  }

  /**
   * Calls the contract off before its exit note (owner, 2026-10-06). Whoever cannot see a
   * cancelled contract is taken back to the list, where it no longer appears.
   */
  async cancelContract(id: string, number: string): Promise<void> {
    const reason = this.cancelReason().trim();
    if (!reason) return;
    const yes = await this.confirm.ask({
      title: this.i18n.t('rent.cancel.confirmTitle', { number }),
      message: this.i18n.t('rent.cancel.confirmMessage', { reason }),
      confirmLabel: this.i18n.t('rent.cancel.action'),
      cancelLabel: this.i18n.t('confirm.keep'),
      danger: true,
    });
    if (!yes) return;
    this.cancelling.set(true);
    this.error.set(null);
    try {
      await this.api.cancelContract(id, reason);
      this.cancelReason.set('');
      if (this.seesCancelled()) await this.reload();
      else this.back.emit();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.cancelling.set(false);
    }
  }

  /** Answer 15: the Admin closes a contract not paid in full, and the reason goes on it. */
  async closeShort(id: string): Promise<void> {
    const reason = this.closeReason().trim();
    if (!reason) return;
    const close = await this.confirm.ask({
      title: this.i18n.t('rent.pay.closeShortTitle'),
      message: this.i18n.t('rent.pay.closeShortMessage', { n: this.money(this.remaining()), reason }),
      confirmLabel: this.i18n.t('rent.pay.closeShort'),
      cancelLabel: this.i18n.t('confirm.keep'),
      danger: true,
    });
    if (!close) return;
    this.closing.set(true);
    this.error.set(null);
    try {
      await this.api.closeShort(id, reason);
      this.closeReason.set('');
      await this.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.closing.set(false);
    }
  }
  readonly i18n = inject(I18nService);
  private readonly regional = inject(RegionalService);
  private readonly printer = inject(PrintService);

  readonly contractId = input.required<string>();
  readonly products = input<AdminProduct[]>([]);
  readonly branches = input<AdminBranch[]>([]);

  readonly back = output<void>();
  readonly editContract = output<string>();
  readonly issueNote = output<string>();
  readonly returnNote = output<string>();

  /** A left-to-right number inside a sentence that may be Arabic: isolated so its digits keep their order. */
  isolate(text: string): string {
    return `\u2066${text}\u2069`;
  }
  readonly extendContract = output<string>();
  /** The contract this one extends or that extended it (answer 8). */
  readonly openContract = output<string>();

  /** The open tab, from the address (`?tab=`); anything unknown is the overview. */
  readonly tab = input<string | null>(null);
  readonly tabChange = output<Tab>();
  readonly tabs = TABS;
  readonly openTab = computed<Tab>(() => {
    const t = this.tab();
    return TABS.includes(t as Tab) ? (t as Tab) : 'overview';
  });

  private readonly chrome = inject(PageChrome, { optional: true });

  constructor() {
    // The page header names the contract: its number as the title, the customer beneath.
    effect(() => {
      const contract = this.docs()?.contract;
      this.chrome?.heading.set(contract ? contract.number : null);
      this.chrome?.description.set(contract ? contract.customerName || '—' : null);
      this.chrome?.trail.set([]);
    });
  }

  ngOnDestroy(): void {
    this.chrome?.description.set(null);
  }

  readonly stateTone = computed(() => toneOf('contract', this.state()));

  /**
   * What this contract allows next, from the same conditions the panel footer always used: edit
   * while not locked; extend and return while some units are out; issue while some are left to go;
   * nothing once it is closed. The first of issue / return is the page's primary action.
   */
  readonly allowed = computed<Action[]>(() => {
    const contract = this.docs()?.contract;
    if (!contract) return [];
    const actions: Action[] = [];
    if (contract.status === 'Cancelled') return actions;
    if (contract.status !== 'Closed') {
      if (this.leftToIssue() > 0) actions.push('issue');
      if (this.stillOut() > 0) actions.push('return', 'extend');
    }
    if (!contract.isLocked) actions.push('edit');
    return actions;
  });
  readonly primaryAction = computed<Action | null>(() => this.allowed().find((a) => a === 'issue' || a === 'return') ?? null);
  readonly moreActions = computed(() => this.allowed().filter((a) => a !== this.primaryAction()));

  actionLabel(a: Action): string {
    return { issue: 'rent.newIssue', return: 'rent.newReturn', extend: 'rent.extend', edit: 'rent.editContract' }[a];
  }

  runAction(a: Action, id: string): void {
    if (a === 'issue') this.issueNote.emit(id);
    else if (a === 'return') this.returnNote.emit(id);
    else if (a === 'extend') this.extendContract.emit(id);
    else this.editContract.emit(id);
  }

  tabCount(t: Tab): number | null {
    const d = this.docs();
    if (!d) return null;
    if (t === 'items') return d.contract.lines.length;
    if (t === 'documents') return d.issueNotes.length + d.returnNotes.length + d.extensions.length || null;
    if (t === 'attachments') return this.attachments().length || null;
    return null;
  }

  /** Arrow keys move between tabs (the ARIA tabs pattern), mirrored in Arabic. */
  onTabKey(event: KeyboardEvent): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    const rtl = document.documentElement.dir === 'rtl';
    const forward = (event.key === 'ArrowRight') !== rtl;
    const i = TABS.indexOf(this.openTab());
    const next = TABS[(i + (forward ? 1 : -1) + TABS.length) % TABS.length];
    event.preventDefault();
    this.tabChange.emit(next);
    setTimeout(() => document.getElementById('rc-tab-' + next)?.focus());
  }

  readonly documentKinds: Documents[] = ['issues', 'returns', 'extensions'];
  readonly documents = signal<Documents>('issues');
  readonly docs = signal<ContractDocuments | null>(null);
  readonly busy = signal(true);
  readonly error = signal<string | null>(null);

  readonly batches = computed<OpenBatch[]>(() => {
    const docs = this.docs();
    return docs ? openBatches(docs, this.regional.settings().timeZone) : [];
  });

  readonly stillOut = computed(() => this.docs()?.settlement.stillOutQty ?? 0);

  readonly leftToIssue = computed(() =>
    (this.docs()?.contract.lines ?? []).reduce((sum, l) => sum + l.leftToIssueQty, 0),
  );

  /** Everything charged for condition rather than for time: what broke, what vanished, repairs. */
  readonly compensation = computed(() => {
    const s = this.docs()?.settlement;
    return s ? s.damage + s.missing + s.maintenance : 0;
  });

  /** Null from the server while the switch is off, which reads as zero and shows nothing. */
  readonly depositHeld = computed(() => this.docs()?.settlement.depositHeld ?? 0);
  readonly depositToReturn = computed(() => this.docs()?.settlement.depositToReturn ?? 0);

  readonly state = computed(() => {
    const docs = this.docs();
    if (!docs) return 'draft' as const;
    return contractState(
      docs.contract,
      docs.settlement.stillOutQty,
      docs.settlement.lines.reduce((sum, l) => sum + l.settledQty, 0),
    );
  });

  readonly tone = computed(() => STATE_TONE[this.state()]);

  /** Who signed, read from the contract row alone — a later representative change alters nothing here. */
  readonly signatory = computed(() => {
    const contract = this.docs()?.contract;
    return contract ? signatoryState(contract) : 'predates';
  });

  readonly signedByKey = signedByKey;

  /** What the first issue note would still be refused for, beside the name. */
  readonly identityGaps = computed(() => {
    const contract = this.docs()?.contract;
    return contract ? identityMissing(contract) : [];
  });

  readonly identityGapWords = computed(() =>
    this.identityGaps().map((field) => this.i18n.t('rent.id.field.' + field)).join(this.i18n.t('rent.list.separator')),
  );

  readonly attachments = computed<RentalAttachment[]>(() => this.docs()?.contract.attachments ?? []);

  /**
   * Whether the identity copies kept with the contract may be listed and opened:
   * customers.documents.view, read from the user's capabilities (owner, 2026-10-25).
   */
  readonly canSeeScans = computed(() => {
    const caps = this.auth.currentUser()?.capabilities;
    return Array.isArray(caps) && caps.includes('customers.documents.view');
  });
  readonly scanBusy = signal(false);
  readonly scanError = signal<string | null>(null);

  /** The limits the server enforces, checked first so a phone photo too large is said at once. */
  private static readonly SCAN_TYPES = ['image/jpeg', 'image/png', 'image/webp', 'application/pdf'];
  private static readonly SCAN_MAX = 5 * 1024 * 1024;

  async uploadScan(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.scanError.set(null);
    if (file.type && !RentalContractComponent.SCAN_TYPES.includes(file.type)) {
      this.scanError.set(this.i18n.t('rent.scan.wrongType'));
      return;
    }
    if (file.size > RentalContractComponent.SCAN_MAX) {
      this.scanError.set(this.i18n.t('rent.scan.tooLarge'));
      return;
    }
    this.scanBusy.set(true);
    try {
      await this.api.uploadAttachment(this.contractId(), file);
      await this.reload();
    } catch (e) {
      this.scanError.set(adminErrorMessage(e));
    } finally {
      this.scanBusy.set(false);
    }
  }

  /** Opened from a blob in a new tab: the file needs the session's token, which a plain link would not carry. */
  async viewScan(file: RentalAttachment): Promise<void> {
    this.scanError.set(null);
    this.scanBusy.set(true);
    try {
      const blob = await this.api.attachmentBlob(this.contractId(), file.id);
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener');
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (e) {
      this.scanError.set(adminErrorMessage(e));
    } finally {
      this.scanBusy.set(false);
    }
  }

  async removeScan(file: RentalAttachment): Promise<void> {
    const yes = await this.confirm.ask({
      ...deleteRequest(this.i18n, this.i18n.t('rent.scan.removeTitle', { name: file.fileName }), this.i18n.t('rent.scan.removeMessage')),
      confirmLabel: this.i18n.t('rent.scan.remove'),
    });
    if (!yes) return;
    this.scanError.set(null);
    this.scanBusy.set(true);
    try {
      await this.api.removeAttachment(this.contractId(), file.id);
      await this.reload();
    } catch (e) {
      this.scanError.set(adminErrorMessage(e));
    } finally {
      this.scanBusy.set(false);
    }
  }

  sizeOf(file: RentalAttachment): string {
    return this.i18n.t('rent.scan.kb', { n: Math.max(1, Math.round(file.length / 1024)) });
  }

  private async reload(): Promise<void> {
    this.docs.set(await this.api.readContract(this.contractId()));
  }

  /** The company's named contract wordings, for the name of the one this contract prints. */
  private readonly wordings = signal<ContractWording[]>([]);
  private readonly storeDocuments = inject(StoreDocumentsApi);
  readonly wordingName = computed(() => {
    const key = this.docs()?.contract.wordingKey;
    if (!key) return '';
    const wording = this.wordings().find((w) => w.key === key);
    // Unread names say nothing rather than a key nobody can read.
    return wording ? this.i18n.text(wording.names) : '';
  });

  async ngOnInit(): Promise<void> {
    // Its own read and its own failure: the contract page never waits on, or loses to, the names.
    void this.storeDocuments.get().then(
      (dto) => this.wordings.set(dto.contractWordings ?? []),
      () => this.wordings.set([]),
    );
    try {
      this.docs.set(await this.api.readContract(this.contractId()));
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  name(lineId: string): string {
    const line = this.docs()?.contract.lines.find((l) => l.id === lineId);
    return line ? lineName(line, this.products(), this.i18n.lang()) : '—';
  }

  branchName(id: string): string {
    const branch = this.branches().find((b) => b.id === id);
    return branch ? localizedName(branch, this.i18n.lang()) : '—';
  }

  /** An accessory inherits its parent's period: a coupler cannot expire before its scaffold. */
  parentDuration(line: RentalContractLine): number {
    const parent = this.docs()?.contract.lines.find((l) => l.id === line.parentLineId);
    return parent?.durationDays ?? line.durationDays;
  }

  due(batch: OpenBatch) {
    return dueState(batch);
  }

  /**
   * What the days already run past the due date would cost if the goods came back today. It is
   * not a charge yet — nothing is charged until a return note settles the units — but a number
   * that only appears after the fact is one nobody can act on.
   */
  overdueSoFar(batch: OpenBatch): number {
    const line = this.docs()?.contract.lines.find((l) => l.id === batch.lineId);
    if (!line) return 0;
    return rentalAmount(line.overdueUnitPrice, batch.daysOverdue, batch.qty, this.regional.decimals());
  }

  /** The issue note a batch left on. Two batches of one item are told apart by nothing else. */
  noteNumber(issueNoteId: string): string {
    return this.docs()?.issueNotes.find((n) => n.id === issueNoteId)?.number ?? '';
  }

  qtyOf(lines: { qty: number }[]): number {
    return lines.reduce((sum, l) => sum + l.qty, 0);
  }

  settledOf(lines: { qtyOk: number; qtyPartialDamage: number; qtyTotalDamage: number; qtyMissing: number }[]): number {
    return lines.reduce((sum, l) => sum + l.qtyOk + l.qtyPartialDamage + l.qtyTotalDamage + l.qtyMissing, 0);
  }

  /**
   * The signed paper, in two copies (docs/07 §4.4): the three unit prices per line, the
   * accessories under their parents, whoever signed, and the owner's own terms. Lock state does
   * not go on it — a contract is signed before it is issued.
   */
  printContract(id: string): void {
    this.printer.open('rental-contract', id, { lang: this.i18n.lang() });
  }

  /**
   * A note is only readable through its contract, so the contract travels beside it; the note's
   * own id stays the document's id.
   */
  printIssueNote(noteId: string, contractId: string): void {
    this.printer.open('issue-note', noteId, { lang: this.i18n.lang(), contract: contractId });
  }

  printReturnNote(noteId: string, contractId: string): void {
    this.printer.open('return-note', noteId, { lang: this.i18n.lang(), contract: contractId });
  }

  day(value: string | null): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  rentalDay(value: string): string {
    return formatRentalDay(value, this.i18n.lang());
  }
}
