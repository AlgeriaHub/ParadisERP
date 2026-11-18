import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { RegionalService } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { DialogComponent } from '../../../ui/dialog.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createPager } from '../../../ui/pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay } from '../backoffice-format';
import { PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, UnitDto } from './manufacturing-api.service';
import { mfgError, qty } from './manufacturing-shared';
import { MfgOrderCostingComponent } from './order-costing.component';
import { MfgOrderFrozenCostComponent } from './order-frozen-cost.component';
import { ConsumptionReason, CostInputs, OrderMaterial, ProductionApiService, ProductionOrder } from './production-api.service';

/** What a quantity dialog does. */
type QtyAction = 'Issue' | 'Return' | 'Scrap' | 'Consume' | 'Reservation' | 'Release';
/** What a reason dialog does. */
type ReasonAction = 'hold' | 'cancel' | 'refuse';

/** Where a line of the order's history comes from: what its filter chooses between. */
type HistoryKind = 'event' | 'movement' | 'consumption' | 'release';
const HISTORY_KINDS: HistoryKind[] = ['event', 'movement', 'consumption', 'release'];
interface HistoryItem {
  kind: HistoryKind;
  at: string;
  what: string;
  detail: string;
  by: string | null;
}

/**
 * One production order (`manufacturing/orders/<id>`): its recipe copy, materials and what became of them,
 * the stages' progress from approved daily records, the output released into the line's stock, expenses,
 * the costing inputs recorded so far and its history. Every action the server would refuse is not offered:
 * the `can…` flags are the server's answer for this reader. Every movement shown says it is not posted yet.
 */
@Component({
  selector: 'app-mfg-order',
  standalone: true,
  imports: [
    FormsModule, ...MENU, DialogComponent, ListFooterComponent, MfgOrderCostingComponent, MfgOrderFrozenCostComponent, StatusComponent,
    TableStateComponent,
  ],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }
      @if (loading()) {
        <section class="ui-surface"><app-table-state kind="loading" /></section>
      } @else if (order(); as o) {
        <section class="bo-panel">
          <div class="bo-panel__body">
            <div class="title-row">
              <app-status [tone]="tone(o.order.status)">{{ i18n.t('mfg.orderStatus.' + o.order.status) }}</app-status>
              @if (o.order.late) {
                <app-status tone="warning">{{ i18n.t('mfg.orders.lateBy', { n: o.order.overdueDays }) }}</app-status>
              }
              <span class="spacer"></span>
              <div class="head-actions">
                @if (o.canManage && o.order.status === 'Open') {
                  <button type="button" class="btn-ghost" (click)="askReason('hold')">{{ i18n.t('mfg.orders.hold') }}</button>
                }
                @if (o.canManage && o.order.status === 'OnHold') {
                  <button type="button" class="btn-ghost" (click)="act('resume')">{{ i18n.t('mfg.orders.resume') }}</button>
                }
                @if (o.canRelease && o.awaitingReleaseBaseQty > 0) {
                  <button type="button" class="btn-ghost" (click)="askQty('Release', null)">{{ i18n.t('mfg.orders.release') }}</button>
                }
                @if (o.canRequestCompletion) {
                  <button type="button" class="btn-primary" (click)="act('request')">{{ i18n.t('mfg.orders.requestCompletion') }}</button>
                }
                @if (o.canApproveCompletion) {
                  <button type="button" class="btn-ghost" (click)="askReason('refuse')">{{ i18n.t('mfg.orders.refuseCompletion') }}</button>
                  <button type="button" class="btn-primary" (click)="act('approve')">{{ i18n.t('mfg.orders.approveCompletion') }}</button>
                }
                @if (o.canManage && o.order.status !== 'Completed') {
                  <button type="button" class="btn-danger" (click)="askReason('cancel')">{{ i18n.t('mfg.orders.cancel') }}</button>
                }
              </div>
            </div>
            <dl class="facts">
              <div><dt>{{ i18n.t('mfg.boms.product') }}</dt><dd>{{ productName() }} <span class="ui-sub mono" dir="ltr">{{ o.order.productSku }}</span></dd></div>
              <div><dt>{{ i18n.t('mfg.boms.line') }}</dt><dd class="mono">{{ o.order.lineCode }}</dd></div>
              <div><dt>{{ i18n.t('mfg.orders.recipe') }}</dt><dd>v{{ o.bomVersion }} · {{ i18n.t('mfg.orders.batches', { n: qty(o.batches) }) }}</dd></div>
              <div><dt>{{ i18n.t('mfg.orders.planned') }}</dt><dd class="mono"><span dir="ltr">{{ qty(o.plannedQty) }} {{ o.plannedUnitCode }}</span></dd></div>
              <div><dt>{{ i18n.t('mfg.orders.plannedDates') }}</dt><dd class="mono">{{ day(o.order.plannedStart) }} → {{ day(o.order.plannedEnd) }}</dd></div>
              <div>
                <dt>{{ i18n.t('mfg.orders.actualEnd') }}</dt>
                <dd class="mono">
                  {{ o.order.actualEnd ? day(o.order.actualEnd) : '—' }}
                  @if (o.order.actualEnd && o.order.overdueDays > 0) {
                    <span class="ui-sub">{{ i18n.t('mfg.orders.overdueBy', { n: o.order.overdueDays }) }}</span>
                  }
                </dd>
              </div>
              @if (o.order.startedShort) {
                <div><dt>{{ i18n.t('mfg.orders.startedShort') }}</dt><dd>{{ o.shortageReason }}</dd></div>
              }
              @if (o.completedBy) {
                <div><dt>{{ i18n.t('mfg.orders.completedBy') }}</dt><dd>{{ o.completedBy }}</dd></div>
              }
            </dl>
            <p class="bo-hint">{{ i18n.t('mfg.orders.notPostedNote') }}</p>
          </div>
        </section>

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.orders.output') }}</h2>
          </div>
          <div class="bo-panel__body">
            <div class="ui-metrics">
              <div class="ui-metric ui-metric--lead"><span class="ui-metric__label">{{ i18n.t('mfg.orders.accepted') }}</span><strong class="ui-metric__value mono" dir="ltr">{{ qty(o.order.acceptedBaseQty) }} {{ o.order.baseUnitCode }}</strong></div>
              <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.orders.produced') }}</span><strong class="ui-metric__value mono" dir="ltr">{{ qty(o.producedBaseQty) }}</strong></div>
              <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.orders.awaitingRelease') }}</span><strong class="ui-metric__value mono" dir="ltr">{{ qty(o.awaitingReleaseBaseQty) }}</strong></div>
              <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.orders.rejected') }}</span><strong class="ui-metric__value mono" dir="ltr">{{ qty(o.order.rejectedBaseQty) }}</strong></div>
              <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.orders.remaining') }}</span><strong class="ui-metric__value mono" dir="ltr">{{ qty(o.remainingBaseQty) }}</strong></div>
            </div>
          </div>
        </section>

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.orders.materials') }}</h2>
          </div>
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.bom.component', { n: '' }) }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.required') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.reserved') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.issued') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.returned') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.consumed') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.scrapped') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.stillAtLine') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (m of o.materials; track m.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfg.bom.component', { n: '' })">
                      {{ materialName(m) }} <span class="ui-sub mono" dir="ltr">{{ m.sku }} · {{ i18n.t('mfg.role.' + m.role) }}</span>
                      @if (m.specification) {
                        <span class="ui-sub">{{ m.specification }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.required')"><span dir="ltr">{{ qty(m.requiredBaseQty) }} {{ m.baseUnitCode }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.reserved')"><span dir="ltr">{{ qty(m.reservedBaseQty) }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.issued')"><span dir="ltr">{{ qty(m.issuedBaseQty) }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.returned')"><span dir="ltr">{{ qty(m.returnedBaseQty) }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.consumed')"><span dir="ltr">{{ qty(m.consumedBaseQty) }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.scrapped')"><span dir="ltr">{{ qty(m.scrappedBaseQty) }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.stillAtLine')"><strong dir="ltr">{{ qty(m.atLineBaseQty) }}</strong></td>
                    <td class="ui-cell-menu">
                      @if (o.canMoveMaterials || o.canManage) {
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + m.sku">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          @if (o.canMoveMaterials) {
                            <button type="button" appMenuItem (click)="askQty('Issue', m)">{{ i18n.t('mfg.orders.issue') }}</button>
                            <button type="button" appMenuItem [disabled]="m.atLineBaseQty <= 0" (click)="askQty('Consume', m)">{{ i18n.t('mfg.orders.consume') }}</button>
                            <button type="button" appMenuItem [disabled]="m.atLineBaseQty <= 0" (click)="askQty('Return', m)">{{ i18n.t('mfg.orders.return') }}</button>
                            <button type="button" appMenuItem [disabled]="m.atLineBaseQty <= 0" (click)="askQty('Scrap', m)">{{ i18n.t('mfg.orders.scrap') }}</button>
                          }
                          @if (o.canManage && m.reservedBaseQty > 0) {
                            <button type="button" appMenuItem (click)="askQty('Reservation', m)">{{ i18n.t('mfg.orders.releaseReservation') }}</button>
                          }
                        </app-menu>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.orders.stages') }}</h2>
          </div>
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.orders.stage') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.plannedHours') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.bookedHours') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.good') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.rejected') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (s of o.stages; track s.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfg.orders.stage')">
                      {{ s.sequence }}. {{ stageName(s) }}
                      @if (s.isOutput) {
                        <span class="ui-sub">{{ i18n.t('mfg.orders.outputStage') }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.plannedHours')">{{ qty(s.plannedMinutes / 60) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.bookedHours')">{{ qty(s.bookedHours) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.good')">{{ qty(s.goodBaseQty) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.rejected')">{{ qty(s.rejectedBaseQty) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.orders.costInputs') }}</h2>
            @if (o.canManage) {
              <button type="button" class="btn-ghost" (click)="expenseOpen.set(true)">+ {{ i18n.t('mfg.orders.addExpense') }}</button>
            }
          </div>
          <div class="bo-panel__body">
            <p class="bo-hint">{{ i18n.t('mfg.orders.costInputsHint') }}</p>
            @if (costs(); as c) {
              <div class="ui-metrics">
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.orders.estimatedMaterials') }}</span><strong class="ui-metric__value mono">{{ i18n.money(c.estimatedMaterialCost) }}</strong></div>
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.orders.usedMaterials') }}</span><strong class="ui-metric__value mono">{{ i18n.money(usedCost(c)) }}</strong></div>
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.orders.labourHours') }}</span><strong class="ui-metric__value mono">{{ qty(c.labourHours) }}</strong></div>
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.orders.expenses') }}</span><strong class="ui-metric__value mono">{{ i18n.money(c.expensesTotal) }}</strong></div>
              </div>
            }
          </div>
          @if (costs(); as c) {
            @if (c.labour.length) {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('mfg.records.employee') }}</th>
                      <th class="num">{{ i18n.t('mfg.records.hours') }}</th>
                      <th>{{ i18n.t('mfg.orders.payrollRef') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (l of c.labour; track $index) {
                      <tr>
                        <td [attr.data-label]="i18n.t('mfg.records.employee')">{{ l.employeeName }} <span class="ui-sub mono" dir="ltr">{{ l.employeeNumber }}</span></td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.records.hours')">{{ qty(l.hours) }}</td>
                        <td class="mono" [attr.data-label]="i18n.t('mfg.orders.payrollRef')">
                          <span dir="ltr">{{ l.payrollYear }}-{{ pad(l.payrollMonth) }}</span>
                          {{ l.payrollRunNumber ? ' · ' + l.payrollRunNumber : ' · ' + i18n.t('mfg.orders.noRunYet') }}
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
            @if (o.expenses.length) {
              <div class="bo-panel__body">
                @for (e of o.expenses; track e.id) {
                  <p class="ui-sub">{{ e.description }} — <span class="mono">{{ i18n.money(e.amount) }}</span> · {{ e.userName }}</p>
                }
              </div>
            }
          }
        </section>

        <app-mfg-order-costing [orderId]="o.order.id" [version]="costVersion()" />
        <app-mfg-order-frozen-cost [orderId]="o.order.id" [version]="costVersion()" />

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.orders.history') }}</h2>
          </div>
          @if (historyKinds().length > 1 || historyKind() !== 'all') {
            <div class="ui-toolbar pane-bar" role="search">
              <div class="ui-filter">
                <label for="oh-kind">{{ i18n.t('filters.kind') }}</label>
                <select id="oh-kind" [ngModel]="historyKind()" (ngModelChange)="historyKind.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  @for (k of historyKinds(); track k) {
                    <option [ngValue]="k">{{ i18n.t('mfg.filter.history.' + k) }}</option>
                  }
                </select>
              </div>
              @if (historyKind() !== 'all') {
                <div class="ui-toolbar__end">
                  <button type="button" class="btn-ghost" (click)="historyKind.set('all')">{{ i18n.t('ui.state.clearFilters') }}</button>
                </div>
              }
            </div>
          }
          @if (history().length && !shownHistory().length) {
            <app-table-state kind="noResults" (clear)="historyKind.set('all')" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('mfg.orders.when') }}</th>
                    <th>{{ i18n.t('mfg.orders.event') }}</th>
                    <th>{{ i18n.t('mfg.orders.detail') }}</th>
                    <th>{{ i18n.t('mfg.orders.by') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (h of historyPager.rows(); track $index) {
                    <tr>
                      <td class="mono" [attr.data-label]="i18n.t('mfg.orders.when')">{{ i18n.dateTime(h.at) }}</td>
                      <td [attr.data-label]="i18n.t('mfg.orders.event')">{{ h.what }}</td>
                      <td [attr.data-label]="i18n.t('mfg.orders.detail')"><span dir="auto">{{ h.detail }}</span></td>
                      <td [attr.data-label]="i18n.t('mfg.orders.by')">{{ h.by || '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <app-list-footer class="pane-foot" [pager]="historyPager" />
          }
        </section>
      }
    </div>

    <app-dialog [open]="!!qtyAction()" size="md" [heading]="qtyHeading()" (dismiss)="qtyAction.set(null)">
      @if (qtyAction(); as a) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          @if (target(); as m) {
            <p class="bo-hint">{{ materialName(m) }} — {{ i18n.t('mfg.orders.stillAtLine') }}: <span class="mono" dir="ltr">{{ qty(m.atLineBaseQty) }} {{ m.baseUnitCode }}</span></p>
          }
          <div class="bo-field">
            <label for="oq-qty">{{ i18n.t('mfg.common.quantity') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <div class="pair">
              <input id="oq-qty" type="number" min="0" step="any" class="num mono" [(ngModel)]="qtyValue" />
              @if (a === 'Issue' || a === 'Return' || a === 'Scrap' || a === 'Release') {
                <select [attr.aria-label]="i18n.t('mfg.common.unit')" [(ngModel)]="qtyUnit">
                  @for (u of unitsFor(); track u.id) {
                    <option [ngValue]="u.id">{{ u.code }}</option>
                  }
                </select>
              } @else {
                <span class="mono unit">{{ target()?.baseUnitCode }}</span>
              }
            </div>
          </div>
          @if (a === 'Consume') {
            <div class="bo-field">
              <label for="oq-reason">{{ i18n.t('mfg.orders.consumeReason') }}</label>
              <select id="oq-reason" [(ngModel)]="consumeReason">
                @for (r of consumeReasons; track r) {
                  <option [ngValue]="r">{{ i18n.t('mfg.consumeReason.' + r) }}</option>
                }
              </select>
            </div>
          }
          @if (a === 'Reservation') {
            <div class="bo-field">
              <label for="oq-why">{{ i18n.t('mfg.orders.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="oq-why" maxlength="400" [(ngModel)]="qtyReason" />
            </div>
          } @else {
            <div class="bo-field">
              <label for="oq-note">{{ i18n.t('mfg.common.note') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="oq-note" maxlength="400" [(ngModel)]="qtyReason" />
            </div>
          }
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="qtyAction.set(null)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy()" (click)="submitQty()">{{ i18n.t('common.confirm') }}</button>
      </div>
    </app-dialog>

    <app-dialog [open]="!!reasonAction()" size="sm" [heading]="reasonAction() ? i18n.t('mfg.orders.' + reasonAction()) : ''" (dismiss)="reasonAction.set(null)">
      @if (reasonAction()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <div class="bo-field">
            <label for="or-why">{{ i18n.t('mfg.orders.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="or-why" maxlength="400" [(ngModel)]="reasonText" />
          </div>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="reasonAction.set(null)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" [class]="reasonAction() === 'cancel' ? 'btn-danger' : 'btn-primary'" [disabled]="busy() || !reasonText.trim()" (click)="submitReason()">{{ i18n.t('common.confirm') }}</button>
      </div>
    </app-dialog>

    <app-dialog [open]="expenseOpen()" size="sm" [heading]="i18n.t('mfg.orders.addExpense')" (dismiss)="expenseOpen.set(false)">
      @if (expenseOpen()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <div class="bo-field">
            <label for="oe-desc">{{ i18n.t('mfg.common.description') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="oe-desc" maxlength="300" [(ngModel)]="expenseText" />
          </div>
          <div class="bo-field">
            <label for="oe-amount">{{ i18n.t('mfg.orders.amount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="oe-amount" type="number" min="0" class="num mono" [step]="regional.moneyStep()" [(ngModel)]="expenseAmount" />
          </div>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="expenseOpen.set(false)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy()" (click)="addExpense()">{{ i18n.t('common.save') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .title-row,
      .head-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
      }
      .spacer {
        flex: 1 1 auto;
      }
      .facts {
        display: grid;
        gap: var(--sp-2) var(--sp-4);
        grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
        margin: var(--sp-3) 0 0;
      }
      .facts dt {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .facts dd {
        margin: 0;
      }
      .editor {
        display: grid;
        gap: var(--sp-3);
      }
      .pair {
        display: flex;
        gap: var(--sp-2);
        align-items: center;
      }
      .pair input {
        flex: 1 1 auto;
        min-inline-size: 0;
      }
      .pair select {
        flex: 0 0 6rem;
      }
      /* The history's filter and its pages, inside the panel's own edges. */
      .pane-bar {
        padding-block: var(--sp-3);
        padding-inline: var(--sp-4);
      }
      .pane-foot {
        padding-block: var(--sp-3);
        padding-inline: var(--sp-4);
      }
    `,
  ],
})
export class MfgOrderComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly production = inject(ProductionApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);

  readonly id = input.required<string>();
  readonly back = output<void>();

  readonly qty = qty;
  readonly consumeReasons: ConsumptionReason[] = ['Used', 'Offcut', 'CountDifference'];
  readonly order = signal<ProductionOrder | null>(null);
  readonly costs = signal<CostInputs | null>(null);
  /** Bumped after each change, so the cost sheet (costing, Phase A) is read again. */
  readonly costVersion = signal(0);
  readonly units = signal<UnitDto[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly formError = signal<string | null>(null);

  readonly qtyAction = signal<QtyAction | null>(null);
  readonly target = signal<OrderMaterial | null>(null);
  qtyValue = 0;
  qtyUnit = '';
  qtyReason = '';
  consumeReason: ConsumptionReason = 'Used';
  readonly reasonAction = signal<ReasonAction | null>(null);
  reasonText = '';
  readonly expenseOpen = signal(false);
  expenseText = '';
  expenseAmount = 0;

  /** Movements, consumption, releases and events, in time order, as one history. */
  readonly history = computed(() => {
    const o = this.order();
    if (!o) return [];
    const t = (k: string, p?: Record<string, string | number>) => this.i18n.t(k, p);
    const items: HistoryItem[] = [
      ...o.events.map((e) => ({ kind: 'event' as const, at: e.atUtc, what: t('mfg.orderEvent.' + e.kind), detail: e.detail ?? '', by: e.userName })),
      ...o.movements.map((m) => ({
        kind: 'movement' as const, at: m.atUtc, what: `${t('mfg.movementKind.' + m.kind)} ${m.number}`,
        detail: m.lines.map((l) => `${l.sku} ${qty(l.baseQty)}`).join(', ') + ' · ' + t('mfg.orders.notPosted'), by: m.userName,
      })),
      ...o.consumptions.map((c) => ({
        kind: 'consumption' as const, at: c.atUtc, what: t('mfg.consumeReason.' + c.reason), detail: `${c.sku} ${qty(c.baseQty)}`, by: c.userName,
      })),
      ...o.releases.map((r) => ({
        kind: 'release' as const, at: r.atUtc, what: `${t('mfg.orders.release')} ${r.number}`,
        detail: `${qty(r.baseQty)} · ${t('mfg.orders.notPosted')}`, by: r.userName,
      })),
    ];
    return items.sort((a, b) => a.at.localeCompare(b.at));
  });

  /** In memory (owner, 2026-11-17): what kind of entry — an event, a movement, a consumption, a release. */
  readonly historyKind = signal<'all' | HistoryKind>('all');
  /** The kinds the history holds, in a fixed order. */
  readonly historyKinds = computed(() => {
    const present = new Set(this.history().map((h) => h.kind));
    return HISTORY_KINDS.filter((k) => present.has(k));
  });
  readonly shownHistory = computed(() => {
    const kind = this.historyKind();
    return this.history().filter((h) => kind === 'all' || h.kind === kind);
  });
  /** Ten entries to begin with; a new filter starts at page one. */
  readonly historyPager = createPager(() => this.shownHistory(), { key: 'mfg.order.history', resetOn: () => [this.historyKind()] });

  async ngOnInit(): Promise<void> {
    try {
      this.api.units().then((u) => this.units.set(u), () => undefined);
      await this.load();
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.loading.set(false);
    }
  }

  private async load(): Promise<void> {
    const o = await this.production.order(this.id());
    this.show(o);
    this.production.costInputs(o.order.id).then((c) => this.costs.set(c), () => undefined);
  }

  private show(o: ProductionOrder): void {
    this.order.set(o);
    this.chrome?.heading.set(`${o.order.number} — ${this.productName()}`);
    this.chrome?.description.set(null);
  }

  productName(): string {
    const o = this.order()?.order;
    return o ? localizedName({ nameAr: o.productNameAr, nameFr: o.productNameFr, nameEn: o.productNameEn }, this.i18n.lang()) || o.productSku : '';
  }

  materialName(m: OrderMaterial): string {
    return localizedName(m, this.i18n.lang()) || m.sku;
  }

  stageName(s: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(s, this.i18n.lang());
  }

  day(value: string): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  pad(n: number): string {
    return String(n).padStart(2, '0');
  }

  tone(status: string) {
    return toneOf('productionOrder', status);
  }

  usedCost(c: CostInputs): number {
    return c.materials.reduce((sum, m) => sum + m.usedCostAtSnapshot, 0);
  }

  qtyHeading(): string {
    const a = this.qtyAction();
    if (!a) return '';
    const key = { Issue: 'issue', Return: 'return', Scrap: 'scrap', Consume: 'consume', Reservation: 'releaseReservation', Release: 'release' }[a];
    return this.i18n.t('mfg.orders.' + key) + (this.target() ? ' — ' + this.target()!.sku : '');
  }

  /** The units a quantity may be typed in: the product's base unit and the units of its dimension. */
  unitsFor(): UnitDto[] {
    const baseId = this.qtyAction() === 'Release' ? null : this.target()?.baseUnitId;
    const base = this.units().find((u) => u.id === (baseId ?? this.qtyUnit));
    return this.units().filter((u) => u.id === base?.id || (base && u.dimension === base.dimension && u.dimension !== 'Other' && u.isActive));
  }

  askQty(action: QtyAction, material: OrderMaterial | null): void {
    const o = this.order();
    this.target.set(material);
    this.qtyValue = action === 'Release' ? (o?.awaitingReleaseBaseQty ?? 0) : action === 'Issue' ? (material?.reservedBaseQty ?? 0) : (material?.atLineBaseQty ?? 0);
    this.qtyUnit = material?.baseUnitId ?? this.units().find((u) => u.code === o?.order.baseUnitCode)?.id ?? '';
    this.qtyReason = '';
    this.consumeReason = 'Used';
    this.formError.set(null);
    this.qtyAction.set(action);
  }

  askReason(action: ReasonAction): void {
    this.reasonText = '';
    this.formError.set(null);
    this.reasonAction.set(action);
  }

  private async run(work: () => Promise<ProductionOrder>, done: string, keepDialog = false): Promise<boolean> {
    this.busy.set(true);
    this.formError.set(null);
    this.error.set(null);
    this.notice.set(null);
    try {
      this.show(await work());
      this.production.costInputs(this.id()).then((c) => this.costs.set(c), () => undefined);
      this.costVersion.update((v) => v + 1);
      this.notice.set(this.i18n.t(done));
      return true;
    } catch (e) {
      const message = mfgError(e, (k) => this.i18n.t(k));
      if (keepDialog) this.formError.set(message);
      else this.error.set(message);
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  async submitQty(): Promise<void> {
    const a = this.qtyAction();
    const o = this.order();
    const m = this.target();
    const value = Number(this.qtyValue);
    if (!a || !o || !(value > 0)) {
      this.formError.set(this.i18n.t('mfg.error.invalid_quantity'));
      return;
    }
    const note = this.qtyReason.trim() || null;
    const id = o.order.id;
    const ok = await this.run(() => {
      switch (a) {
        case 'Release': return this.production.release(id, value, this.qtyUnit, note);
        case 'Consume': return this.production.consume(id, m!.id, value, this.consumeReason, null, note);
        case 'Reservation': return this.production.releaseReservation(id, m!.id, value, note ?? '');
        default: return this.production.move(id, a, [{ orderMaterialId: m!.id, qty: value, unitId: this.qtyUnit }], note);
      }
    }, 'mfg.orders.done', true);
    if (ok) this.qtyAction.set(null);
  }

  async submitReason(): Promise<void> {
    const a = this.reasonAction();
    const id = this.order()?.order.id;
    if (!a || !id) return;
    const reason = this.reasonText.trim();
    const ok = await this.run(
      () => (a === 'hold' ? this.production.hold(id, reason) : a === 'cancel' ? this.production.cancel(id, reason) : this.production.refuseCompletion(id, reason)),
      'mfg.orders.done', true);
    if (ok) this.reasonAction.set(null);
  }

  async act(kind: 'resume' | 'request' | 'approve'): Promise<void> {
    const o = this.order();
    if (!o) return;
    const label = this.i18n.t(kind === 'resume' ? 'mfg.orders.resume' : kind === 'request' ? 'mfg.orders.requestCompletion' : 'mfg.orders.approveCompletion');
    if (!(await this.confirm.ask(actionRequest(this.i18n, label, o.order.number)))) return;
    const id = o.order.id;
    await this.run(
      () => (kind === 'resume' ? this.production.resume(id) : kind === 'request' ? this.production.requestCompletion(id) : this.production.approveCompletion(id)),
      'mfg.orders.done');
  }

  async addExpense(): Promise<void> {
    const id = this.order()?.order.id;
    if (!id) return;
    const ok = await this.run(() => this.production.addExpense(id, this.expenseText.trim(), Number(this.expenseAmount)), 'mfg.orders.done', true);
    if (ok) {
      this.expenseOpen.set(false);
      this.expenseText = '';
      this.expenseAmount = 0;
    }
  }
}
