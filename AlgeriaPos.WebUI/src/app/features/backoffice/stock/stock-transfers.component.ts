import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { CatalogStore } from '../../../core/catalog-store';
import { I18nService } from '../../../core/i18n.service';
import { SearchableProduct } from '../../../core/product-search';
import { PrintService } from '../../print/print.service';
import { ProductPickerComponent } from '../../../shared/product-picker.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { actionRequest, removeLineRequest } from '../../../ui/confirm-requests';
import { ConfirmService } from '../../../ui/confirm.service';
import { MultilingualName, localizedName } from '../backoffice-format';
import {
  ReceiveTransferLine,
  StockTransfer,
  StockTransferEvent,
  StockTransferEventKind,
  StockTransferLine,
  StockTransferStatus,
  StockTransfersApi,
} from './stock-transfers-api.service';
import { StockTransferPagesApi } from './stock-transfer-pages.api';

interface DispatchFormLine {
  productId: string;
  qty: number;
  sku?: string;
  name?: string;
}

/**
 * Inventory › Stock transfers (owner spec 2026-10-12, "Stock transfers with dispatch and receive").
 *
 * <p>Transfers goods between branches and warehouses with a full audit trail (dispatch at source,
 * receipt at destination, declaration of transit loss, and cancellation before receipt).</p>
 *
 * <p>A shortfall (owner, 2026-10-13): the destination reports what is missing with the receipt, with
 * a reason; it stays in transit, tagged "shortfall reported", until the source confirms it as lost or
 * rejects it. Whether this user may answer comes from the server per transfer
 * (`canDecideShortfall`), and both answers go through the confirm dialog.</p>
 */
@Component({
  selector: 'app-backoffice-stock-transfers',
  standalone: true,
  imports: [FormsModule, StatusComponent, ProductPickerComponent, DateInputComponent, ListFooterComponent, TableStateComponent],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }
      @if (done()) {
        <div class="notice ok" role="status">
          <span>{{ done() }}</span>
          @if (lastCreatedTransfer(); as created) {
            <button type="button" class="btn-ghost btn-sm" (click)="printTransfer(created.id)">
              {{ i18n.t('transfer.print') }}
            </button>
          }
        </div>
      }

      <!-- ==================== DISPATCH PANEL ==================== -->
      @if (dispatchOpen()) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('transfer.action.new') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('transfer.hint.dispatch') }}</p>
            <button type="button" class="btn-ghost btn-sm" (click)="closeDispatch()">
              {{ i18n.t('transfer.close') }}
            </button>
          </div>

          <div class="bo-panel__body bo-form">
            @if (dispatchError()) {
              <p class="notice danger" role="alert">{{ dispatchError() }}</p>
            }

            <div class="bo-fields bo-fields--roomy">
              @if (lock.locked()) {
                <div class="bo-field">
                  <p class="bo-fixed-label">{{ i18n.t('transfer.fromBranch') }}</p>
                  <p class="bo-fixed" id="trf-from-fixed">{{ lock.ownBranchName(branches()) }}</p>
                </div>
              } @else {
                <div class="bo-field">
                  <label for="trf-from-branch">
                    {{ i18n.t('transfer.fromBranch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select
                    id="trf-from-branch"
                    [ngModel]="dispatchFromBranchId()"
                    (ngModelChange)="setDispatchFromBranch($event)"
                  >
                    @for (b of branches(); track b.id) {
                      <option [ngValue]="b.id">{{ name(b) }}</option>
                    }
                  </select>
                </div>
              }

              <div class="bo-field">
                <label for="trf-to-branch">
                  {{ i18n.t('transfer.toBranch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <select id="trf-to-branch" [(ngModel)]="dispatchToBranchId">
                  <option value="" disabled>{{ i18n.t('transfer.toBranch') }}</option>
                  @for (b of availableDestinationBranches(); track b.id) {
                    <option [ngValue]="b.id">{{ name(b) }}</option>
                  }
                </select>
              </div>

              <div class="bo-field bo-field--wide">
                <label for="trf-dispatch-note">{{ i18n.t('transfer.col.note') }}</label>
                <input
                  id="trf-dispatch-note"
                  type="text"
                  maxlength="400"
                  [(ngModel)]="dispatchNote"
                  [placeholder]="i18n.t('transfer.col.note')"
                />
              </div>
            </div>

            <div class="bo-stack">
              <h3 class="bo-lines-heading">{{ i18n.t('transfer.col.lines') }}</h3>
              @for (line of dispatchLines(); track $index; let i = $index) {
                <div class="bo-line-grid">
                  <div class="bo-field bo-field--wide">
                    <label [for]="'trf-prod-' + i">
                      {{ i18n.t('receipt.item') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <app-product-picker
                      [inputId]="'trf-prod-' + i"
                      [products]="products()"
                      [refresh]="refresh"
                      [productId]="line.productId"
                      (picked)="setLineProduct(i, $event)"
                    />
                  </div>

                  <div class="bo-field">
                    <label [for]="'trf-qty-' + i">
                      {{ i18n.t('register.qty') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <input
                      [id]="'trf-qty-' + i"
                      type="number"
                      min="1"
                      step="1"
                      class="num mono"
                      [(ngModel)]="line.qty"
                    />
                  </div>

                  @if (dispatchLines().length > 1) {
                    <div class="bo-field bo-field-action">
                      <button type="button" class="btn-ghost btn-sm" (click)="removeLine(i)">
                        {{ i18n.t('transfer.removeLine') }}
                      </button>
                    </div>
                  }
                </div>
              }

              <div>
                <button type="button" class="btn-ghost btn-sm" (click)="addLine()">
                  {{ i18n.t('transfer.addLine') }}
                </button>
              </div>
            </div>
          </div>

          <div class="bo-panel__foot">
            <button type="button" class="btn-ghost" (click)="closeDispatch()">
              {{ i18n.t('common.cancel') }}
            </button>
            <button
              type="button"
              class="btn-primary"
              [disabled]="!canSubmitDispatch()"
              (click)="submitDispatch()"
            >
              {{ i18n.t('transfer.dispatch') }}
            </button>
          </div>
        </section>
      }

      <!-- ==================== DETAIL PANEL ==================== -->
      @else if (selectedTransfer(); as t) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">
              {{ i18n.t('transfer.detail.title') }} — <span class="mono">{{ t.number }}</span>
            </h2>
            <p class="bo-panel__note">
              <bdi>{{ branchName(t.fromBranchId, t.fromBranchName) }}</bdi>
              <span class="ic-dir" aria-hidden="true">&rarr;</span>
              <bdi>{{ branchName(t.toBranchId, t.toBranchName) }}</bdi>
            </p>
            <div class="bo-panel-buttons">
              <button type="button" class="btn-ghost btn-sm" (click)="printTransfer(t.id)">
                {{ i18n.t('transfer.print') }}
              </button>
              <button type="button" class="btn-ghost btn-sm" (click)="closeDetail()">
                {{ i18n.t('transfer.backToList') }}
              </button>
            </div>
          </div>

          <div class="bo-panel__body">
            @if (actionError()) {
              <p class="notice danger" role="alert">{{ actionError() }}</p>
            }

            <div class="bo-facts-strip">
              <div>
                <span class="bo-meta-label">{{ i18n.t('transfer.filter.status') }}:</span>
                <span class="bo-tag-row">
                  <app-status [tone]="toneOf('transfer', t.status)">{{ statusWord(t.status) }}</app-status>
                  @if (hasPendingShortfall(t)) {
                    <span class="sr-only"> — </span>
                    <app-status [tone]="toneOf('transferShortfall', 'reported')">{{ i18n.t('transfer.shortfall.tag') }}</app-status>
                  }
                </span>
              </div>
              <div>
                <span class="bo-meta-label">{{ i18n.t('transfer.col.from') }}:</span>
                <strong>{{ branchName(t.fromBranchId, t.fromBranchName) }}</strong>
              </div>
              <div>
                <span class="bo-meta-label">{{ i18n.t('transfer.col.to') }}:</span>
                <strong>{{ branchName(t.toBranchId, t.toBranchName) }}</strong>
              </div>
              <div>
                <span class="bo-meta-label">{{ i18n.t('transfer.col.dispatchedAt') }}:</span>
                <span class="mono">{{ i18n.dateTime(t.dispatchedAtUtc) }}</span>
                @if (t.dispatchedByName) {
                  <span class="bo-soft">({{ t.dispatchedByName }})</span>
                }
              </div>
              @if (t.closedAtUtc) {
                <div>
                  <span class="bo-meta-label">{{ i18n.t('transfer.detail.closedAt') }}:</span>
                  <span class="mono">{{ i18n.dateTime(t.closedAtUtc) }}</span>
                </div>
              }
              @if (t.note) {
                <div class="bo-fact-wide">
                  <span class="bo-meta-label">{{ i18n.t('transfer.col.note') }}:</span>
                  <span>{{ t.note }}</span>
                </div>
              }
            </div>

            <!-- Lines Table -->
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('backoffice.products.sku') }}</th>
                    <th>{{ i18n.t('receipt.item') }}</th>
                    <th class="num">{{ i18n.t('transfer.col.dispatched') }}</th>
                    <th class="num">{{ i18n.t('transfer.col.received') }}</th>
                    <th class="num">{{ i18n.t('transfer.col.lost') }}</th>
                    <th class="num">{{ i18n.t('transfer.col.returned') }}</th>
                    <th class="num">{{ i18n.t('transfer.col.inTransit') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (line of t.lines; track line.id) {
                    <tr>
                      <td class="mono" [attr.data-label]="i18n.t('backoffice.products.sku')">{{ line.sku }}</td>
                      <td [attr.data-label]="i18n.t('receipt.item')">{{ name(line) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('transfer.col.dispatched')">{{ line.qtyDispatched }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('transfer.col.received')">{{ line.qtyReceived }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('transfer.col.lost')">{{ line.qtyLost }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('transfer.col.returned')">{{ line.qtyReturned }}</td>
                      <td
                        class="num mono bo-cell-strong"
                        [class.bo-in-transit-pos]="line.inTransit > 0"
                        [attr.data-label]="i18n.t('transfer.col.inTransit')"
                      >
                        {{ line.inTransit }}
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>

            <!-- Shortfall reported at receipt (owner, 2026-10-13): what was reported, how it was answered,
                 and — while it waits — the answer, offered to whom the server allows. -->
            @if (hasShortfallHistory(t)) {
              <div
                class="bo-action-box"
                [class.bo-action-box--danger]="hasPendingShortfall(t)"
                id="trf-shortfall"
              >
                <h3 class="bo-sub-title bo-tag-row">
                  <span>{{ i18n.t('transfer.shortfall.title') }}</span>
                  @if (hasPendingShortfall(t)) {
                    <span class="sr-only"> — </span>
                    <app-status [tone]="toneOf('transferShortfall', 'reported')">{{ i18n.t('transfer.shortfall.tag') }}</app-status>
                  }
                </h3>
                <div class="bo-scroll">
                  <table class="bo-data">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('backoffice.products.sku') }}</th>
                        <th>{{ i18n.t('receipt.item') }}</th>
                        <th class="num">{{ i18n.t('transfer.shortfall.col.reported') }}</th>
                        <th class="num">{{ i18n.t('transfer.shortfall.col.confirmed') }}</th>
                        <th class="num">{{ i18n.t('transfer.shortfall.col.rejected') }}</th>
                        <th class="num">{{ i18n.t('transfer.shortfall.col.pending') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (line of shortfallLines(t); track line.id) {
                        <tr>
                          <td class="mono" [attr.data-label]="i18n.t('backoffice.products.sku')">{{ line.sku }}</td>
                          <td [attr.data-label]="i18n.t('receipt.item')">{{ name(line) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('transfer.shortfall.col.reported')">{{ line.qtyShortReported }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('transfer.shortfall.col.confirmed')">{{ line.qtyShortConfirmed }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('transfer.shortfall.col.rejected')">{{ line.qtyShortRejected }}</td>
                          <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('transfer.shortfall.col.pending')">{{ line.qtyShortPending }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>

                @if (hasPendingShortfall(t)) {
                  @if (lastReport(t); as report) {
                    <p class="bo-hint">
                      {{ i18n.t('transfer.shortfall.lastReport', { name: report.userName || '—', date: i18n.dateTime(report.atUtc), reason: report.reason || '—' }) }}
                    </p>
                  }
                  @if (canDecideShortfall(t)) {
                    <div class="bo-fields bo-fields--roomy">
                      <div class="bo-field bo-field--wide">
                        <label for="shortfall-answer">{{ i18n.t('transfer.shortfall.answer') }}</label>
                        <input
                          id="shortfall-answer"
                          type="text"
                          maxlength="400"
                          aria-describedby="shortfall-answer-hint"
                          [(ngModel)]="shortfallAnswer"
                        />
                        <p class="bo-hint" id="shortfall-answer-hint">{{ i18n.t('transfer.shortfall.answerHint') }}</p>
                      </div>
                    </div>
                    <div class="bo-sub-foot">
                      <button type="button" class="btn-ghost" [disabled]="busy()" (click)="rejectShortfall(t)">
                        {{ i18n.t('transfer.shortfall.reject') }}
                      </button>
                      <button type="button" class="btn-primary btn-danger" [disabled]="busy()" (click)="confirmShortfall(t)">
                        {{ i18n.t('transfer.shortfall.confirm') }}
                      </button>
                    </div>
                  } @else {
                    <p class="bo-empty-note">{{ i18n.t('transfer.shortfall.waiting') }}</p>
                  }
                }
              </div>
            }

            <!-- Action buttons -->
            <div class="bo-action-bar">
              @if (canReceive(t)) {
                <button
                  type="button"
                  class="btn-primary"
                  (click)="toggleReceiveOpen()"
                >
                  {{ i18n.t('transfer.receive') }}
                </button>
              }
              @if (canDeclareLost(t)) {
                <button
                  type="button"
                  class="btn-ghost"
                  (click)="toggleLostOpen()"
                >
                  {{ i18n.t('transfer.declareLost') }}
                </button>
              }
              @if (canCancel(t)) {
                <button
                  type="button"
                  class="btn-ghost btn-danger"
                  (click)="toggleCancelOpen()"
                >
                  {{ i18n.t('transfer.cancel') }}
                </button>
              }
            </div>

            <!-- Sub-form: Receive -->
            @if (receiveOpen()) {
              <div class="bo-action-box">
                <div class="bo-action-box__head">
                  <h3 class="bo-sub-title">{{ i18n.t('transfer.receive.title') }}</h3>
                  @if (inTransitLines(t).length > 1) {
                    <button type="button" class="btn-ghost btn-sm" (click)="setAllReceived(t)">
                      {{ i18n.t('transfer.receiveAll') }}
                    </button>
                  }
                </div>
                <p class="bo-hint">{{ i18n.t('transfer.hint.receive') }}</p>
                @if (inTransitLines(t).length === 0) {
                  <p class="bo-empty-note">{{ i18n.t('transfer.noInTransit') }}</p>
                } @else {
                  <div class="bo-stack">
                    @for (line of inTransitLines(t); track line.id) {
                      <div class="bo-receive-line">
                        <p class="bo-receive-line__name" [id]="'rcv-name-' + line.id">
                          <span class="mono">{{ line.sku }}</span> — {{ name(line) }}
                          <span class="bo-hint">
                            ({{ i18n.t('transfer.col.inTransit') }}: {{ line.inTransit }})
                            @if (line.qtyShortPending > 0) {
                              ({{ i18n.t('transfer.shortfall.col.pending') }}: {{ line.qtyShortPending }})
                            }
                          </span>
                        </p>
                        <div class="bo-field">
                          <label [for]="'rcv-' + line.id">{{ i18n.t('transfer.qtyToReceive') }}</label>
                          <input
                            [id]="'rcv-' + line.id"
                            type="number"
                            min="0"
                            [max]="line.inTransit"
                            class="num mono"
                            [attr.aria-describedby]="'rcv-name-' + line.id"
                            [ngModel]="receiveQtys()[line.id]"
                            (ngModelChange)="setReceiveQty(line.id, $event)"
                          />
                        </div>
                        <div class="bo-field">
                          <label [for]="'short-' + line.id">{{ i18n.t('transfer.qtyShort') }}</label>
                          <input
                            [id]="'short-' + line.id"
                            type="number"
                            min="0"
                            [max]="reportableShort(line)"
                            class="num mono"
                            [attr.aria-describedby]="'rcv-name-' + line.id"
                            [ngModel]="shortQtys()[line.id]"
                            (ngModelChange)="setShortQty(line.id, $event)"
                          />
                        </div>
                      </div>
                    }
                  </div>
                  <div class="bo-fields bo-fields--roomy">
                    <div class="bo-field bo-field--wide">
                      <label for="rcv-short-reason">
                        {{ i18n.t('transfer.shortReason') }}
                        @if (anyShort()) {
                          <span class="bo-req">{{ i18n.t('common.required') }}</span>
                        }
                      </label>
                      <input id="rcv-short-reason" type="text" maxlength="400" [required]="anyShort()" [(ngModel)]="shortReason" />
                    </div>
                    <div class="bo-field bo-field--wide">
                      <label for="rcv-note">{{ i18n.t('transfer.receiveNote') }}</label>
                      <input id="rcv-note" type="text" maxlength="400" [(ngModel)]="receiveNote" />
                    </div>
                  </div>
                  <div class="bo-sub-foot">
                    <button type="button" class="btn-ghost" (click)="receiveOpen.set(false)">
                      {{ i18n.t('common.cancel') }}
                    </button>
                    <button
                      type="button"
                      class="btn-primary"
                      [disabled]="busy() || !hasValidReceiveQtys()"
                      (click)="submitReceive(t)"
                    >
                      {{ i18n.t('transfer.receive') }}
                    </button>
                  </div>
                }
              </div>
            }

            <!-- Sub-form: Declare Lost -->
            @if (lostOpen()) {
              <div class="bo-action-box bo-action-box--danger">
                <h3 class="bo-sub-title">{{ i18n.t('transfer.lost.title') }}</h3>
                <p class="bo-hint">{{ i18n.t('transfer.hint.lost') }}</p>
                @if (inTransitLines(t).length === 0) {
                  <p class="bo-empty-note">{{ i18n.t('transfer.noInTransit') }}</p>
                } @else {
                  <div class="bo-fields bo-fields--roomy">
                    @for (line of inTransitLines(t); track line.id) {
                      <div class="bo-field">
                        <label [for]="'lost-' + line.id">
                          <span class="mono">{{ line.sku }}</span> — {{ name(line) }}
                          <span class="bo-hint">({{ i18n.t('transfer.col.inTransit') }}: {{ line.inTransit }})</span>
                        </label>
                        <input
                          [id]="'lost-' + line.id"
                          type="number"
                          min="0"
                          [max]="line.inTransit"
                          class="num mono"
                          [ngModel]="lostQtys()[line.id]"
                          (ngModelChange)="setLostQty(line.id, $event)"
                        />
                      </div>
                    }
                    <div class="bo-field bo-field--wide">
                      <label for="lost-reason">
                        {{ i18n.t('transfer.lostReason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                      </label>
                      <input id="lost-reason" type="text" [(ngModel)]="lostReason" required />
                    </div>
                  </div>
                  <div class="bo-sub-foot">
                    <button type="button" class="btn-ghost" (click)="lostOpen.set(false)">
                      {{ i18n.t('common.cancel') }}
                    </button>
                    <button
                      type="button"
                      class="btn-primary btn-danger"
                      [disabled]="busy() || !hasValidLostQtys() || !lostReason().trim()"
                      (click)="submitDeclareLost(t)"
                    >
                      {{ i18n.t('transfer.declareLost') }}
                    </button>
                  </div>
                }
              </div>
            }

            <!-- Sub-form: Cancel Transfer -->
            @if (cancelOpen()) {
              <div class="bo-action-box bo-action-box--danger">
                <h3 class="bo-sub-title">{{ i18n.t('transfer.cancel.title') }}</h3>
                <p class="bo-hint">{{ i18n.t('transfer.hint.cancel') }}</p>
                <div class="bo-fields bo-fields--roomy">
                  <div class="bo-field bo-field--wide">
                    <label for="cancel-reason">{{ i18n.t('transfer.cancelReason') }}</label>
                    <input id="cancel-reason" type="text" [(ngModel)]="cancelReason" />
                  </div>
                </div>
                <div class="bo-sub-foot">
                  <button type="button" class="btn-ghost" (click)="cancelOpen.set(false)">
                    {{ i18n.t('common.cancel') }}
                  </button>
                  <button
                    type="button"
                    class="btn-primary btn-danger"
                    [disabled]="busy()"
                    (click)="submitCancel(t)"
                  >
                    {{ i18n.t('transfer.cancel') }}
                  </button>
                </div>
              </div>
            }

            <!-- Events History -->
            <div class="bo-events-block">
              <h3 class="bo-sub-title">{{ i18n.t('transfer.history.title') }}</h3>
              @if (t.events && t.events.length) {
                <div class="bo-scroll">
                  <table class="bo-data">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('print.transfer.date') }}</th>
                        <th>{{ i18n.t('print.transfer.status') }}</th>
                        <th>{{ i18n.t('transfer.col.by') }}</th>
                        <th>{{ i18n.t('transfer.col.reason') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (e of t.events; track e.id) {
                        <tr>
                          <td class="mono" [attr.data-label]="i18n.t('print.transfer.date')">{{ i18n.dateTime(e.atUtc) }}</td>
                          <td [attr.data-label]="i18n.t('print.transfer.status')">{{ eventWord(e.kind) }}</td>
                          <td [attr.data-label]="i18n.t('transfer.col.by')">{{ e.userName || '—' }}</td>
                          <td [attr.data-label]="i18n.t('transfer.col.reason')">{{ e.reason || '—' }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              } @else {
                <p class="bo-empty-note">{{ i18n.t('common.nothingHere') }}</p>
              }
            </div>
          </div>
        </section>
      }

      <!-- ==================== LIST PANEL ==================== -->
      @else {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('transfer.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('transfer.lead') }}</p>
            <button type="button" class="btn-primary btn-sm" (click)="openDispatch()">
              {{ i18n.t('transfer.action.new') }}
            </button>
          </div>

          <div class="bo-panel__body bo-panel__body--filters">
            <div class="trf-filter-grid">
              @if (lock.locked()) {
                <p class="bo-scope-note">
                  {{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}
                </p>
              } @else {
                <div class="bo-field">
                  <label for="trf-filter-branch">{{ i18n.t('transfer.filter.branch') }}</label>
                  <select
                    id="trf-filter-branch"
                    [ngModel]="filterBranchId()"
                    (ngModelChange)="setFilterBranch($event)"
                  >
                    <option [ngValue]="null">{{ i18n.t('reports.allBranches') }}</option>
                    @for (b of branches(); track b.id) {
                      <option [ngValue]="b.id">{{ name(b) }}</option>
                    }
                  </select>
                </div>
              }

              <div class="bo-field">
                <label for="trf-filter-status">{{ i18n.t('transfer.filter.status') }}</label>
                <select
                  id="trf-filter-status"
                  [ngModel]="filterStatus()"
                  (ngModelChange)="setFilterStatus($event)"
                >
                  <option value="all">{{ i18n.t('transfer.filter.allStatuses') }}</option>
                  <option value="open">{{ i18n.t('transfer.filter.open') }}</option>
                  <option value="shortfall">{{ i18n.t('transfer.filter.shortfall') }}</option>
                  <option value="Dispatched">{{ i18n.t('transfer.status.Dispatched') }}</option>
                  <option value="PartlyReceived">{{ i18n.t('transfer.status.PartlyReceived') }}</option>
                  <option value="Received">{{ i18n.t('transfer.status.Received') }}</option>
                  <option value="Cancelled">{{ i18n.t('transfer.status.Cancelled') }}</option>
                </select>
              </div>

              <div class="bo-field">
                <label for="trf-filter-from">{{ i18n.t('transfer.filter.from') }}</label>
                <app-date-input
                  id="trf-filter-from"
                  [ngModel]="filterFrom()"
                  (ngModelChange)="setFilterFrom($event)"
                />
              </div>

              <div class="bo-field">
                <label for="trf-filter-to">{{ i18n.t('transfer.filter.to') }}</label>
                <app-date-input
                  id="trf-filter-to"
                  [ngModel]="filterTo()"
                  (ngModelChange)="setFilterTo($event)"
                />
              </div>

              <!-- Sent or received, seen from the branch in view (owner, 2026-11-17): the user's own
                   branch, or the one chosen above. Applied by the server, like every filter here. -->
              @if (referenceBranchId()) {
                <div class="bo-field">
                  <label for="trf-filter-direction">{{ i18n.t('transfer.filter.direction') }}</label>
                  <select
                    id="trf-filter-direction"
                    [ngModel]="directionFilter()"
                    (ngModelChange)="directionFilter.set($event)"
                  >
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    <option ngValue="out">{{ i18n.t('transfer.filter.outgoing') }}</option>
                    <option ngValue="in">{{ i18n.t('transfer.filter.incoming') }}</option>
                  </select>
                </div>
              }
            </div>
            @if (listFiltered()) {
              <div class="trf-filter-end">
                <button type="button" class="btn-ghost btn-sm" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
              </div>
            }
          </div>

          @if (!pager.loaded()) {
            <p class="bo-loading" role="status">
              <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
              <span class="bo-loading__rows" aria-hidden="true"></span>
            </p>
          } @else if (!pager.total() && !listFiltered()) {
            <p class="bo-panel__body bo-empty-note">{{ i18n.t('common.nothingHere') }}</p>
          } @else if (!pager.total()) {
            <div class="bo-panel__body">
              <app-table-state kind="noResults" (clear)="clearFilters()" />
            </div>
          } @else {
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('transfer.col.number') }}</th>
                    <th>{{ i18n.t('transfer.col.dispatchedAt') }}</th>
                    <th>{{ i18n.t('transfer.col.from') }}</th>
                    <th>{{ i18n.t('transfer.col.to') }}</th>
                    <th class="num">{{ i18n.t('transfer.col.lines') }}</th>
                    <th class="num">{{ i18n.t('transfer.col.dispatched') }}</th>
                    <th class="num">{{ i18n.t('transfer.col.inTransit') }}</th>
                    <th>{{ i18n.t('transfer.filter.status') }}</th>
                    <th class="bo-cell-actions">{{ i18n.t('transfer.col.actions') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (t of pager.rows(); track t.id) {
                    <tr class="bo-row-clickable" (click)="selectTransfer(t)">
                      <td class="mono bo-cell-strong" [attr.data-label]="i18n.t('transfer.col.number')">
                        {{ t.number }}
                      </td>
                      <td class="mono" [attr.data-label]="i18n.t('transfer.col.dispatchedAt')">
                        {{ i18n.date(t.dispatchedAtUtc) }}
                      </td>
                      <td [attr.data-label]="i18n.t('transfer.col.from')">{{ branchName(t.fromBranchId, t.fromBranchName) }}</td>
                      <td [attr.data-label]="i18n.t('transfer.col.to')">{{ branchName(t.toBranchId, t.toBranchName) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('transfer.col.lines')">
                        {{ t.lines.length }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('transfer.col.dispatched')">
                        {{ t.qtyDispatched }}
                      </td>
                      <td
                        class="num mono bo-cell-strong"
                        [class.bo-in-transit-pos]="t.inTransit > 0"
                        [attr.data-label]="i18n.t('transfer.col.inTransit')"
                      >
                        {{ t.inTransit }}
                      </td>
                      <td [attr.data-label]="i18n.t('transfer.filter.status')">
                        <span class="bo-states">
                          <app-status [tone]="toneOf('transfer', t.status)">
                            {{ statusWord(t.status) }}
                          </app-status>
                          @if (hasPendingShortfall(t)) {
                            <span class="sr-only"> — </span>
                            <app-status [tone]="toneOf('transferShortfall', 'reported')">
                              {{ i18n.t('transfer.shortfall.tag') }}
                            </app-status>
                          }
                        </span>
                      </td>
                      <td class="bo-cell-actions">
                        <button
                          type="button"
                          class="btn-ghost btn-sm"
                          (click)="selectTransfer(t); $event.stopPropagation()"
                        >
                          {{ i18n.t('transfer.view') }}
                        </button>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <app-list-footer class="trf-list-foot" [pager]="pager" [loading]="!pager.loaded()" />
          }
        </section>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      .bo-empty-note {
        margin: 0;
        color: var(--muted);
      }
      .bo-row-clickable {
        cursor: pointer;
      }
      .bo-row-clickable:hover {
        background: var(--surface-2);
      }
      .bo-panel-buttons {
        display: flex;
        align-items: center;
        gap: var(--sp-2);
      }
      .bo-lines-heading,
      .bo-sub-title {
        margin: 0;
        font-size: 15px;
        font-weight: 600;
      }
      .bo-line-grid {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 10rem auto;
        gap: var(--sp-3);
        align-items: end;
      }
      .bo-field-action {
        margin-block-end: var(--sp-2);
      }
      .bo-facts-strip {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-4) var(--sp-6);
        padding: var(--sp-4);
        background: var(--surface);
        border-radius: var(--radius-card);
        border: 1px solid var(--border);
      }
      .bo-meta-label {
        font-size: var(--fs-meta);
        color: var(--muted);
        margin-inline-end: var(--sp-2);
      }
      .bo-fact-wide {
        flex-basis: 100%;
      }
      .bo-in-transit-pos {
        color: var(--warn);
      }
      .bo-action-bar {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-3);
        margin-block-start: var(--sp-3);
      }
      .bo-action-box {
        display: grid;
        gap: var(--sp-4);
        padding: var(--sp-5);
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--radius-card);
      }
      .bo-action-box--danger {
        border-color: var(--danger-soft);
      }
      .bo-sub-foot {
        display: flex;
        justify-content: flex-end;
        gap: var(--sp-3);
        margin-block-start: var(--sp-2);
      }
      /* The arrow between the two branches points the reading way (spec: directional glyphs mirror in RTL). */
      .ic-dir {
        display: inline-block;
      }
      :host-context([dir='rtl']) .ic-dir {
        transform: scaleX(-1);
      }
      .bo-events-block {
        display: grid;
        gap: var(--sp-3);
        margin-block-start: var(--sp-4);
      }
      .bo-action-box__head {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: var(--sp-3);
        margin-block-end: var(--sp-2);

        .bo-sub-title {
          margin: 0;
        }
      }
      /* A status and the shortfall tag beside it, on one line with the label before them. */
      .bo-tag-row {
        display: inline-flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
        vertical-align: middle;
      }
      /* One line of a receipt: the article, what arrived, what is reported short. */
      .bo-receive-line {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 9rem 9rem;
        gap: var(--sp-3);
        align-items: end;
      }
      .bo-receive-line__name {
        margin: 0;
        align-self: center;
      }
      @media (max-width: 640px) {
        .bo-receive-line {
          grid-template-columns: 1fr 1fr;
        }
        .bo-receive-line__name {
          grid-column: 1 / -1;
        }
      }
      .bo-panel__body--filters {
        padding-block: var(--sp-4);
      }
      .trf-filter-grid {
        display: grid;
        grid-template-columns: 1fr;
        gap: var(--sp-3) var(--sp-4);
        inline-size: 100%;

        @media (min-width: 640px) {
          grid-template-columns: repeat(2, 1fr);
        }

        @media (min-width: 1024px) {
          grid-template-columns: repeat(4, minmax(0, 1fr));
        }
      }
      .trf-filter-end {
        display: flex;
        justify-content: flex-end;
        margin-block-start: var(--sp-3);
      }
      .trf-list-foot {
        padding: var(--sp-4) var(--sp-5);
        border-block-start: var(--line) solid var(--border);
      }
    `,
  ],
})
export class BackofficeStockTransfersComponent implements OnInit {
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly auth = inject(AuthService);
  private readonly admin = inject(AdminService);
  private readonly transfersApi = inject(StockTransfersApi);
  private readonly pagesApi = inject(StockTransferPagesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly print = inject(PrintService);
  private readonly catalog = inject(CatalogStore);

  readonly products = this.catalog.products;
  readonly refresh = () => this.catalog.refresh();

  readonly branches = signal<AdminBranch[]>([]);
  readonly selectedTransfer = signal<StockTransfer | null>(null);

  // Panels visibility
  readonly dispatchOpen = signal(false);
  readonly receiveOpen = signal(false);
  readonly lostOpen = signal(false);
  readonly cancelOpen = signal(false);

  // States
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly lastCreatedTransfer = signal<StockTransfer | null>(null);
  readonly dispatchError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);

  // List filters
  readonly filterBranchId = signal<string | null>(null);
  readonly filterStatus = signal<string>('all');
  readonly filterFrom = signal<string>('');
  readonly filterTo = signal<string>('');
  /** Sent from or received at the branch in view; the server reads it against the branch it lists for. */
  readonly directionFilter = signal<'all' | 'out' | 'in'>('all');

  /** The branch a direction is read from: the user's own, or the one the Admin chose. None, no direction. */
  readonly referenceBranchId = computed(() => this.lock.lockedBranchId() ?? this.filterBranchId());

  readonly listFiltered = computed(
    () =>
      (!!this.referenceBranchId() && this.directionFilter() !== 'all') ||
      (!this.lock.locked() && !!this.filterBranchId()) ||
      this.filterStatus() !== 'all' ||
      !!this.filterFrom() ||
      !!this.filterTo(),
  );

  /**
   * The transfers a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"): ten rows to begin with; every filter runs there, over every transfer, and a change
   * goes back to page one. A branch user's branch is the server's, whatever is sent.
   */
  readonly pager = createServerPager(
    (page) => this.pagesApi.page({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'inventory.transfers',
      filters: () => ({
        branchId: this.filterBranchId(),
        status: this.filterStatus(),
        from: this.filterFrom(),
        to: this.filterTo(),
        direction: this.referenceBranchId() ? this.directionFilter() : ('all' as const),
      }),
    },
  );

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  // Dispatch form signals
  readonly dispatchFromBranchId = signal<string>('');
  readonly dispatchToBranchId = signal<string>('');
  readonly dispatchNote = signal<string>('');
  readonly dispatchLines = signal<DispatchFormLine[]>([{ productId: '', qty: 1 }]);

  // Action form signals (receive / lost / cancel / the answer to a shortfall)
  readonly receiveQtys = signal<Record<string, number>>({});
  /** What the destination reports short with the receipt, per line (owner, 2026-10-13). */
  readonly shortQtys = signal<Record<string, number>>({});
  readonly shortReason = signal<string>('');
  readonly receiveNote = signal<string>('');
  readonly lostQtys = signal<Record<string, number>>({});
  readonly lostReason = signal<string>('');
  readonly cancelReason = signal<string>('');
  /** Why the source confirms (optional) or rejects (required) a reported shortfall. */
  readonly shortfallAnswer = signal<string>('');

  /**
   * Capability to declare in-transit stock lost. Read directly from currentUser's capabilities,
   * never a role check.
   */
  readonly canWriteOff = computed(() => {
    const caps = this.auth.currentUser()?.capabilities;
    return Array.isArray(caps) && caps.includes('inventory.transfer.write_off');
  });

  readonly availableDestinationBranches = computed(() => {
    const fromId = this.lock.locked() ? this.lock.lockedBranchId() : this.dispatchFromBranchId();
    return this.branches().filter((b) => b.id !== fromId);
  });

  readonly canSubmitDispatch = computed(() => {
    if (this.busy()) return false;
    const fromId = this.lock.locked() ? (this.lock.lockedBranchId() ?? '') : this.dispatchFromBranchId();
    const toId = this.dispatchToBranchId();
    if (!fromId || !toId || fromId === toId) return false;
    const lines = this.dispatchLines();
    return lines.length > 0 && lines.some((l) => !!l.productId && Number(l.qty) > 0);
  });

  readonly toneOf = toneOf;

  async ngOnInit(): Promise<void> {
    try {
      const [branches] = await Promise.all([this.admin.listBranches(), this.catalog.ensure()]);
      this.branches.set(branches);
      this.initDispatchBranchDefaults(branches);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
    // The list loads itself through its pager.
  }

  private initDispatchBranchDefaults(branches: AdminBranch[]): void {
    if (this.lock.locked()) {
      const lockedId = this.lock.lockedBranchId() ?? '';
      this.dispatchFromBranchId.set(lockedId);
      const other = branches.find((b) => b.id !== lockedId);
      if (other) this.dispatchToBranchId.set(other.id);
    } else if (branches.length > 0) {
      this.dispatchFromBranchId.set(branches[0].id);
      if (branches.length > 1) {
        this.dispatchToBranchId.set(branches[1].id);
      }
    }
  }

  // ---------- Translations & Helpers ----------

  name(record: MultilingualName): string {
    return localizedName(record, this.i18n.lang());
  }

  /** The branch in the reader's language when the page knows it; the server's name otherwise. */
  branchName(id: string, fallback: string): string {
    const branch = this.branches().find((b) => b.id === id);
    return branch ? this.name(branch) : fallback;
  }

  statusWord(status: StockTransferStatus): string {
    return this.i18n.t(`transfer.status.${status}`);
  }

  eventWord(kind: StockTransferEventKind): string {
    return this.i18n.t(`transfer.event.${kind}`);
  }

  isOpen(t: StockTransfer): boolean {
    return t.status === 'Dispatched' || t.status === 'PartlyReceived';
  }

  canReceive(t: StockTransfer): boolean {
    if (!this.isOpen(t)) return false;
    return !this.lock.locked() || this.lock.lockedBranchId() === t.toBranchId;
  }

  canDeclareLost(t: StockTransfer): boolean {
    if (!this.isOpen(t)) return false;
    if (!this.canWriteOff()) return false;
    return !this.lock.locked() || this.lock.lockedBranchId() === t.fromBranchId;
  }

  canCancel(t: StockTransfer): boolean {
    if (t.status !== 'Dispatched') return false;
    return !this.lock.locked() || this.lock.lockedBranchId() === t.fromBranchId;
  }

  inTransitLines(t: StockTransfer): StockTransferLine[] {
    return t.lines.filter((l) => l.inTransit > 0);
  }

  // ---------- Shortfall reported at receipt (owner, 2026-10-13) ----------

  /** The destination reported something short and the source has not answered yet. */
  hasPendingShortfall(t: StockTransfer): boolean {
    return t.qtyShortPending > 0;
  }

  /** Anything was ever reported short on this transfer — answered or not. */
  hasShortfallHistory(t: StockTransfer): boolean {
    return t.lines.some((l) => l.qtyShortReported > 0);
  }

  shortfallLines(t: StockTransfer): StockTransferLine[] {
    return t.lines.filter((l) => l.qtyShortReported > 0);
  }

  /** What the destination said when it last reported something short. */
  lastReport(t: StockTransfer): StockTransferEvent | null {
    const reports = (t.events ?? []).filter((e) => e.kind === 'ShortfallReported');
    return reports.length ? reports[reports.length - 1] : null;
  }

  /**
   * Whether to offer confirm and reject: the server's answer for this user and this transfer
   * (`canDecideShortfall` — the source branch, or a write-off holder; never the destination). The
   * page repeats no rule of its own.
   */
  canDecideShortfall(t: StockTransfer): boolean {
    return t.canDecideShortfall === true && this.hasPendingShortfall(t);
  }

  /** What may still be reported short on a line once the quantity entered as received is taken off. */
  reportableShort(line: StockTransferLine): number {
    const left = Math.max(0, line.inTransit - (Number(this.receiveQtys()[line.id]) || 0));
    return Math.max(0, left - Math.min(line.qtyShortPending, left));
  }

  anyShort(): boolean {
    return Object.values(this.shortQtys()).some((q) => Number(q) > 0);
  }

  // ---------- Filters & List Loading ----------
  // Each filter is one of the pager's: setting it asks the server again, from page one.

  setFilterBranch(branchId: string | null): void {
    this.filterBranchId.set(branchId);
  }

  setFilterStatus(status: string): void {
    this.filterStatus.set(status);
  }

  setFilterFrom(from: string): void {
    this.filterFrom.set(from ?? '');
  }

  setFilterTo(to: string): void {
    this.filterTo.set(to ?? '');
  }

  /** Every filter back to "all"; a locked user's branch is not a filter and stays. */
  clearFilters(): void {
    this.directionFilter.set('all');
    if (!this.lock.locked()) this.filterBranchId.set(null);
    this.filterStatus.set('all');
    this.filterFrom.set('');
    this.filterTo.set('');
  }

  /** Asks again for the page on screen, after a dispatch, a receipt, a loss, a cancel or an answer. */
  async loadTransfers(): Promise<void> {
    await this.pager.reload();
  }

  // ---------- Dispatch Panel ----------

  openDispatch(): void {
    this.dispatchOpen.set(true);
    this.selectedTransfer.set(null);
    this.error.set(null);
    this.dispatchError.set(null);
    this.done.set(null);
    this.dispatchLines.set([{ productId: '', qty: 1 }]);
    this.dispatchNote.set('');
    this.initDispatchBranchDefaults(this.branches());
  }

  closeDispatch(): void {
    this.dispatchOpen.set(false);
    this.dispatchError.set(null);
  }

  setDispatchFromBranch(branchId: string): void {
    this.dispatchFromBranchId.set(branchId);
    if (this.dispatchToBranchId() === branchId) {
      const other = this.branches().find((b) => b.id !== branchId);
      this.dispatchToBranchId.set(other?.id ?? '');
    }
  }

  setLineProduct(index: number, product: SearchableProduct): void {
    this.dispatchLines.update((lines) => {
      const copy = [...lines];
      copy[index] = {
        ...copy[index],
        productId: product.id,
        sku: product.sku,
        name: localizedName(product, this.i18n.lang()),
      };
      return copy;
    });
  }

  addLine(): void {
    this.dispatchLines.update((lines) => [...lines, { productId: '', qty: 1 }]);
  }

  async removeLine(index: number): Promise<void> {
    const line = this.dispatchLines()[index];
    if (line.productId || line.qty > 1) {
      const ok = await this.confirm.ask(removeLineRequest(this.i18n));
      if (!ok) return;
    }
    this.dispatchLines.update((lines) => lines.filter((_, i) => i !== index));
  }

  async submitDispatch(): Promise<void> {
    const fromId = this.lock.locked() ? (this.lock.lockedBranchId() ?? '') : this.dispatchFromBranchId();
    const toId = this.dispatchToBranchId();
    if (!fromId || !toId || fromId === toId) {
      this.dispatchError.set(this.i18n.t('transfer.destinationMustDiffer'));
      return;
    }

    const lines = this.dispatchLines()
      .filter((l) => !!l.productId && Number(l.qty) > 0)
      .map((l) => ({ productId: l.productId, qty: Number(l.qty) }));

    if (lines.length === 0) {
      this.dispatchError.set(this.i18n.t('transfer.noLines'));
      return;
    }

    const ok = await this.confirm.ask(
      actionRequest(
        this.i18n,
        this.i18n.t('transfer.dispatch'),
        this.i18n.t('transfer.dispatch.confirmMessage'),
      ),
    );
    if (!ok) return;

    this.busy.set(true);
    this.dispatchError.set(null);
    try {
      const created = await this.transfersApi.dispatch({
        fromBranchId: fromId,
        toBranchId: toId,
        note: this.dispatchNote().trim() || null,
        lines,
      });
      this.dispatchOpen.set(false);
      this.lastCreatedTransfer.set(created);
      this.done.set(this.i18n.t('transfer.dispatched', { number: created.number }));
      await this.loadTransfers();
    } catch (e) {
      this.dispatchError.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  // ---------- Detail Panel & Actions ----------

  async selectTransfer(summary: StockTransfer): Promise<void> {
    this.selectedTransfer.set(summary);
    this.dispatchOpen.set(false);
    this.receiveOpen.set(false);
    this.lostOpen.set(false);
    this.cancelOpen.set(false);
    this.error.set(null);
    this.actionError.set(null);
    this.initActionForms(summary);

    try {
      const full = await this.transfersApi.get(summary.id);
      this.selectedTransfer.set(full);
      this.initActionForms(full);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  closeDetail(): void {
    this.selectedTransfer.set(null);
    this.receiveOpen.set(false);
    this.lostOpen.set(false);
    this.cancelOpen.set(false);
    this.actionError.set(null);
  }

  private initActionForms(t: StockTransfer): void {
    const rMap: Record<string, number> = {};
    const sMap: Record<string, number> = {};
    const lMap: Record<string, number> = {};
    for (const line of t.lines) {
      if (line.inTransit > 0) {
        // What is reported short is not assumed to have turned up: the receipt starts on the rest.
        rMap[line.id] = Math.max(0, line.inTransit - line.qtyShortPending);
        sMap[line.id] = 0;
        lMap[line.id] = 0;
      }
    }
    this.receiveQtys.set(rMap);
    this.shortQtys.set(sMap);
    this.lostQtys.set(lMap);
    this.receiveNote.set('');
    this.shortReason.set('');
    this.lostReason.set('');
    this.cancelReason.set('');
    this.shortfallAnswer.set('');
  }

  toggleReceiveOpen(): void {
    this.receiveOpen.update((v) => !v);
    this.lostOpen.set(false);
    this.cancelOpen.set(false);
    this.actionError.set(null);
  }

  toggleLostOpen(): void {
    this.lostOpen.update((v) => !v);
    this.receiveOpen.set(false);
    this.cancelOpen.set(false);
    this.actionError.set(null);
  }

  toggleCancelOpen(): void {
    this.cancelOpen.update((v) => !v);
    this.receiveOpen.set(false);
    this.lostOpen.set(false);
    this.actionError.set(null);
  }

  setReceiveQty(lineId: string, qty: number): void {
    this.receiveQtys.update((m) => ({ ...m, [lineId]: qty }));
  }

  setAllReceived(t: StockTransfer): void {
    const next: Record<string, number> = {};
    for (const line of this.inTransitLines(t)) {
      next[line.id] = line.inTransit;
    }
    this.receiveQtys.set(next);
  }

  setShortQty(lineId: string, qty: number): void {
    this.shortQtys.update((m) => ({ ...m, [lineId]: qty }));
  }

  setLostQty(lineId: string, qty: number): void {
    this.lostQtys.update((m) => ({ ...m, [lineId]: qty }));
  }

  /** Something arrived, or something is reported short. */
  hasValidReceiveQtys(): boolean {
    return Object.values(this.receiveQtys()).some((q) => Number(q) > 0) || this.anyShort();
  }

  hasValidLostQtys(): boolean {
    return Object.values(this.lostQtys()).some((q) => Number(q) > 0);
  }

  async submitReceive(t: StockTransfer): Promise<void> {
    // Each line once: what arrived, and what is reported short when there is any.
    const received = this.receiveQtys();
    const short = this.shortQtys();
    const lines: ReceiveTransferLine[] = [];
    for (const lineId of new Set([...Object.keys(received), ...Object.keys(short)])) {
      const qty = Number(received[lineId]) || 0;
      const missing = Number(short[lineId]) || 0;
      if (qty <= 0 && missing <= 0) continue;
      lines.push(missing > 0 ? { lineId, qty, short: missing } : { lineId, qty });
    }

    if (lines.length === 0) {
      this.actionError.set(this.i18n.t('transfer.qtyRequired'));
      return;
    }
    const reportsShort = lines.some((l) => (l.short ?? 0) > 0);
    const shortReason = this.shortReason().trim();
    if (reportsShort && !shortReason) {
      this.actionError.set(this.i18n.t('transfer.shortReasonRequired'));
      return;
    }

    const ok = await this.confirm.ask(
      actionRequest(
        this.i18n,
        this.i18n.t('transfer.receive'),
        this.i18n.t('transfer.receive.confirmMessage'),
      ),
    );
    if (!ok) return;

    this.busy.set(true);
    this.actionError.set(null);
    try {
      const updated = await this.transfersApi.receive(t.id, {
        lines,
        note: this.receiveNote().trim() || null,
        shortReason: reportsShort ? shortReason : null,
      });
      this.receiveOpen.set(false);
      this.done.set(this.i18n.t(reportsShort ? 'transfer.received.withShortfall' : 'transfer.received'));
      this.selectedTransfer.set(updated);
      this.initActionForms(updated);
      await this.loadTransfers();
    } catch (e) {
      this.actionError.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async submitDeclareLost(t: StockTransfer): Promise<void> {
    const lines = Object.entries(this.lostQtys())
      .filter(([_, qty]) => Number(qty) > 0)
      .map(([lineId, qty]) => ({ lineId, qty: Number(qty) }));

    if (lines.length === 0) {
      this.actionError.set(this.i18n.t('transfer.qtyRequired'));
      return;
    }
    const reason = this.lostReason().trim();
    if (!reason) {
      this.actionError.set(this.i18n.t('transfer.reasonRequired'));
      return;
    }

    const ok = await this.confirm.ask(
      actionRequest(
        this.i18n,
        this.i18n.t('transfer.declareLost'),
        this.i18n.t('transfer.lost.confirmMessage'),
        true,
      ),
    );
    if (!ok) return;

    this.busy.set(true);
    this.actionError.set(null);
    try {
      const updated = await this.transfersApi.declareLost(t.id, {
        lines,
        reason,
      });
      this.lostOpen.set(false);
      this.done.set(this.i18n.t('transfer.declaredLost'));
      this.selectedTransfer.set(updated);
      this.initActionForms(updated);
      await this.loadTransfers();
    } catch (e) {
      this.actionError.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async submitCancel(t: StockTransfer): Promise<void> {
    const ok = await this.confirm.ask(
      actionRequest(
        this.i18n,
        this.i18n.t('transfer.cancel'),
        this.i18n.t('transfer.cancel.confirmMessage'),
        true,
      ),
    );
    if (!ok) return;

    this.busy.set(true);
    this.actionError.set(null);
    try {
      const updated = await this.transfersApi.cancel(t.id, {
        reason: this.cancelReason().trim() || undefined,
      });
      this.cancelOpen.set(false);
      this.done.set(this.i18n.t('transfer.cancelled'));
      this.selectedTransfer.set(updated);
      this.initActionForms(updated);
      await this.loadTransfers();
    } catch (e) {
      this.actionError.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * The source confirms the shortfall shown: written off as lost at the source. Sends the quantities
   * the page displayed, so a report made after it was read is not confirmed with it.
   */
  async confirmShortfall(t: StockTransfer): Promise<void> {
    const lines = this.pendingShortLines(t);
    if (lines.length === 0) return;
    const ok = await this.confirm.ask(
      actionRequest(
        this.i18n,
        this.i18n.t('transfer.shortfall.confirm'),
        this.i18n.t('transfer.shortfall.confirmMessage'),
        true,
      ),
    );
    if (!ok) return;
    await this.answerShortfall(
      () => this.transfersApi.confirmShortfall(t.id, { lines, reason: this.shortfallAnswer().trim() || null }),
      'transfer.shortfall.confirmed',
    );
  }

  /** The source rejects the shortfall shown, with a reason: it stays in transit for the destination. */
  async rejectShortfall(t: StockTransfer): Promise<void> {
    const reason = this.shortfallAnswer().trim();
    if (!reason) {
      this.actionError.set(this.i18n.t('transfer.reasonRequired'));
      return;
    }
    const lines = this.pendingShortLines(t);
    if (lines.length === 0) return;
    const ok = await this.confirm.ask(
      actionRequest(
        this.i18n,
        this.i18n.t('transfer.shortfall.reject'),
        this.i18n.t('transfer.shortfall.rejectMessage'),
      ),
    );
    if (!ok) return;
    await this.answerShortfall(
      () => this.transfersApi.rejectShortfall(t.id, { lines, reason }),
      'transfer.shortfall.rejected',
    );
  }

  private pendingShortLines(t: StockTransfer): { lineId: string; qty: number }[] {
    return t.lines.filter((l) => l.qtyShortPending > 0).map((l) => ({ lineId: l.id, qty: l.qtyShortPending }));
  }

  private async answerShortfall(call: () => Promise<StockTransfer>, doneKey: string): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const updated = await call();
      this.done.set(this.i18n.t(doneKey));
      this.selectedTransfer.set(updated);
      this.initActionForms(updated);
      await this.loadTransfers();
    } catch (e) {
      this.actionError.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  printTransfer(id: string): void {
    this.print.open('stock-transfer', id);
  }
}
