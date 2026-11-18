import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { RegionalService } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { DialogComponent } from '../../../ui/dialog.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay } from '../backoffice-format';
import { PageChrome } from '../shell/page-chrome';
import { MFG, mfgCan, mfgError, qty } from './manufacturing-shared';
import {
  LogEmployeeCandidate,
  LogRowRequest,
  NON_ORDER_CATEGORIES,
  NonOrderCategory,
  OrderStage,
  ProductionApiService,
  ProductionLog,
  ProductionLogRow,
  ProductionLogWarning,
  ProductionOrderSummary,
} from './production-api.service';

/** A row as the editor holds it until saved. */
interface RowDraft {
  id: string | null;
  kind: 'order' | 'other';
  orderId: string | null;
  stageId: string | null;
  category: NonOrderCategory;
  specification: string;
  plannedQty: number;
  completedQty: number;
  rejectedQty: number;
  rejectReason: string;
  notes: string;
}

/**
 * One daily production record (`manufacturing/records/<id>`; owner, 2026-10-26, decision 4): the line's day
 * — each operation a row: an order and its stage, the product and its specification, the planned, completed
 * and rejected quantities, the employees and their hours, the material consumption recorded against it, and
 * notes. Only the output stage's completed quantity becomes finished output, so units passing several stages
 * count once; a stage ahead of the one before is said. A production manager of the line who did not submit
 * it approves it, and it is frozen; until then a manager may send it back.
 *
 * Hours are booked on a saved row of a draft. The day's attendance is shown beside each person; more than it
 * shows is refused unless the reader may override and says why.
 */
@Component({
  selector: 'app-mfg-record',
  standalone: true,
  imports: [FormsModule, DialogComponent, StatusComponent, TableStateComponent],
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
      } @else if (log(); as l) {
        <section class="bo-panel">
          <div class="bo-panel__body">
            <div class="title-row">
              <app-status [tone]="tone(l.log.status)">{{ i18n.t('mfg.logStatus.' + l.log.status) }}</app-status>
              <span class="spacer"></span>
              <div class="head-actions">
                @if (l.canEdit) {
                  <button type="button" class="btn-ghost" [disabled]="busy()" (click)="save()">{{ i18n.t('common.save') }}</button>
                  <button type="button" class="btn-primary" [disabled]="busy() || !l.rows.length" (click)="submit()">{{ i18n.t('mfg.records.submit') }}</button>
                }
                @if (l.canApprove) {
                  <button type="button" class="btn-ghost" (click)="returning.set(true)">{{ i18n.t('mfg.records.sendBack') }}</button>
                  <button type="button" class="btn-primary" [disabled]="busy()" (click)="approve()">{{ i18n.t('mfg.records.approve') }}</button>
                }
              </div>
            </div>
            <dl class="facts">
              <div><dt>{{ i18n.t('mfg.boms.line') }}</dt><dd class="mono">{{ l.log.lineCode }}</dd></div>
              <div><dt>{{ i18n.t('mfg.records.day') }}</dt><dd class="mono">{{ day(l.log.day) }}</dd></div>
              <div><dt>{{ i18n.t('mfg.records.submittedBy') }}</dt><dd>{{ l.log.submittedBy || '—' }}</dd></div>
              <div><dt>{{ i18n.t('mfg.records.approvedBy') }}</dt><dd>{{ l.log.approvedBy || '—' }}</dd></div>
              <div><dt>{{ i18n.t('mfg.records.hours') }}</dt><dd class="mono">{{ qty(l.log.hours) }}</dd></div>
            </dl>
            @if (l.returnedReason) {
              <p class="notice warn">{{ i18n.t('mfg.records.returned', { reason: l.returnedReason }) }}</p>
            }
            @for (w of l.warnings; track $index) {
              <p class="notice warn">{{ warning(w) }}</p>
            }
            @if (l.log.status === 'Submitted' && !l.canApprove) {
              <p class="bo-hint">{{ i18n.t('mfg.records.waitingApproval') }}</p>
            }
          </div>
        </section>

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.records.operations') }}</h2>
          </div>
          <div class="bo-panel__body">
            <p class="bo-hint">{{ i18n.t('mfg.records.operationsHint') }}</p>
            @for (r of rows; track $index; let i = $index) {
              <div class="row-card">
                <div class="row-grid">
                  <div class="bo-field">
                    <label [for]="'rr-k-' + i">{{ i18n.t('mfg.records.kind') }}</label>
                    <select [id]="'rr-k-' + i" [disabled]="!l.canEdit || used(r)" [(ngModel)]="r.kind">
                      <option ngValue="order">{{ i18n.t('mfg.records.kindOrder') }}</option>
                      <option ngValue="other">{{ i18n.t('mfg.records.kindOther') }}</option>
                    </select>
                  </div>
                  @if (r.kind === 'order') {
                    <div class="bo-field">
                      <label [for]="'rr-o-' + i">{{ i18n.t('mfg.orders.number') }}</label>
                      <select [id]="'rr-o-' + i" [disabled]="!l.canEdit || used(r)" [(ngModel)]="r.orderId" (ngModelChange)="orderChanged(r)">
                        @for (o of orderChoices(r); track o.id) {
                          <option [ngValue]="o.id">{{ o.number }} — {{ o.productSku }}</option>
                        }
                      </select>
                    </div>
                    <div class="bo-field">
                      <label [for]="'rr-s-' + i">{{ i18n.t('mfg.orders.stage') }}</label>
                      <select [id]="'rr-s-' + i" [disabled]="!l.canEdit || used(r)" [(ngModel)]="r.stageId">
                        @for (s of stagesOf(r.orderId); track s.id) {
                          <option [ngValue]="s.id">{{ s.sequence }}. {{ stageName(s) }}{{ s.isOutput ? ' — ' + i18n.t('mfg.orders.outputStage') : '' }}</option>
                        }
                      </select>
                    </div>
                  } @else {
                    <div class="bo-field">
                      <label [for]="'rr-c-' + i">{{ i18n.t('mfg.records.category') }}</label>
                      <select [id]="'rr-c-' + i" [disabled]="!l.canEdit || used(r)" [(ngModel)]="r.category">
                        @for (c of categories; track c) {
                          <option [ngValue]="c">{{ i18n.t('mfg.category.' + c) }}</option>
                        }
                      </select>
                    </div>
                  }
                  <div class="bo-field">
                    <label [for]="'rr-p-' + i">{{ i18n.t('mfg.records.planned') }}</label>
                    <input [id]="'rr-p-' + i" type="number" min="0" step="any" class="num mono" [disabled]="!l.canEdit" [(ngModel)]="r.plannedQty" />
                  </div>
                  @if (r.kind === 'order') {
                    <div class="bo-field">
                      <label [for]="'rr-d-' + i">{{ i18n.t('mfg.records.completed') }}</label>
                      <input [id]="'rr-d-' + i" type="number" min="0" step="any" class="num mono" [disabled]="!l.canEdit" [(ngModel)]="r.completedQty" />
                    </div>
                    <div class="bo-field">
                      <label [for]="'rr-x-' + i">{{ i18n.t('mfg.records.rejected') }}</label>
                      <input [id]="'rr-x-' + i" type="number" min="0" step="any" class="num mono" [disabled]="!l.canEdit" [(ngModel)]="r.rejectedQty" />
                    </div>
                    @if (r.rejectedQty > 0) {
                      <div class="bo-field">
                        <label [for]="'rr-xr-' + i">{{ i18n.t('mfg.records.rejectReason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                        <input [id]="'rr-xr-' + i" maxlength="300" [disabled]="!l.canEdit" [(ngModel)]="r.rejectReason" />
                      </div>
                    }
                    <div class="bo-field row-wide">
                      <label [for]="'rr-sp-' + i">{{ i18n.t('mfg.records.specification') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                      <input [id]="'rr-sp-' + i" maxlength="600" [disabled]="!l.canEdit" [(ngModel)]="r.specification" [placeholder]="i18n.t('mfg.records.specificationHint')" />
                    </div>
                  }
                  <div class="bo-field row-wide">
                    <label [for]="'rr-n-' + i">{{ i18n.t('mfg.common.note') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                    <input [id]="'rr-n-' + i" maxlength="400" [disabled]="!l.canEdit" [(ngModel)]="r.notes" />
                  </div>
                </div>

                @if (saved(r); as s) {
                  <div class="row-people">
                    <span class="row-title">{{ i18n.t('mfg.records.employees') }}</span>
                    @for (b of s.bookings; track b.id) {
                      <div class="booking">
                        <span>{{ b.employeeName }} <span class="ui-sub mono" dir="ltr">{{ b.employeeNumber }}</span></span>
                        <span class="mono">{{ i18n.t('mfg.records.hoursOf', { n: qty(b.hours) }) }}</span>
                        @if (b.overtimeHours > 0) {
                          <span class="ui-sub">{{ i18n.t('mfg.records.overtimeOf', { n: qty(b.overtimeHours) }) }}</span>
                        }
                        @if (b.overrideReason) {
                          <span class="ui-sub">{{ i18n.t('mfg.records.overridden', { by: b.overriddenBy ?? '', reason: b.overrideReason }) }}</span>
                        }
                        @if (l.canEdit) {
                          <button type="button" class="btn-ghost bo-small" (click)="unbook(b.id)">{{ i18n.t('mfg.common.remove') }}</button>
                        }
                      </div>
                    } @empty {
                      <span class="ui-sub">{{ i18n.t('mfg.records.noEmployees') }}</span>
                    }
                    @if (l.canEdit) {
                      <div>
                        <button type="button" class="btn-ghost bo-small" (click)="openBooking(s)">+ {{ i18n.t('mfg.records.addHours') }}</button>
                      </div>
                    }
                    @if (s.consumptions.length) {
                      <span class="row-title">{{ i18n.t('mfg.records.consumption') }}</span>
                      @for (c of s.consumptions; track c.id) {
                        <span class="ui-sub"><span dir="ltr" class="mono">{{ c.sku }} {{ qty(c.baseQty) }}</span> · {{ i18n.t('mfg.consumeReason.' + c.reason) }}</span>
                      }
                    }
                  </div>
                } @else if (l.canEdit) {
                  <p class="bo-hint">{{ i18n.t('mfg.records.saveFirst') }}</p>
                }
                @if (l.canEdit && !used(r)) {
                  <div class="row-remove">
                    <button type="button" class="btn-ghost bo-small" (click)="rows.splice(i, 1)">{{ i18n.t('mfg.common.remove') }}</button>
                  </div>
                }
              </div>
            } @empty {
              <p class="bo-hint">{{ i18n.t('mfg.records.noRows') }}</p>
            }
            @if (l.canEdit) {
              <div>
                <button type="button" class="btn-ghost" (click)="addRow()">+ {{ i18n.t('mfg.records.addRow') }}</button>
              </div>
            }
            @if (l.canEdit) {
              <div class="bo-field">
                <label for="rr-notes">{{ i18n.t('mfg.common.note') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                <input id="rr-notes" maxlength="1000" [(ngModel)]="notes" />
              </div>
            } @else if (l.notes) {
              <p>{{ l.notes }}</p>
            }
          </div>
        </section>
      }
    </div>

    <app-dialog [open]="!!bookingRow()" size="md" [heading]="i18n.t('mfg.records.addHours')" (dismiss)="bookingRow.set(null)">
      @if (bookingRow()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <div class="bo-field">
            <label for="bk-emp">{{ i18n.t('mfg.records.employee') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <select id="bk-emp" [(ngModel)]="bookEmployee">
              @for (c of candidates(); track c.employeeId) {
                <option [ngValue]="c.employeeId" [disabled]="c.payrollLocked">{{ c.fullName }} · {{ attendanceText(c) }}</option>
              }
            </select>
            @if (!candidates().length) {
              <p class="bo-hint">{{ i18n.t('mfg.records.noCandidates') }}</p>
            }
          </div>
          @if (chosen(); as c) {
            <p class="bo-hint">{{ i18n.t('mfg.records.capacity', { attendance: c.attendanceHours === null ? '—' : qty(c.attendanceHours), booked: qty(c.bookedHours) }) }}</p>
          }
          <div class="bo-field">
            <label for="bk-hours">{{ i18n.t('mfg.records.hours') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="bk-hours" type="number" min="0" max="24" step="0.25" class="num mono" [(ngModel)]="bookHours" />
          </div>
          <!-- C-05: how many of these hours were overtime; payroll's premium can then be kept to them in costing. -->
          <div class="bo-field">
            <label for="bk-overtime">{{ i18n.t('mfg.records.overtimeHours') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
            <input id="bk-overtime" type="number" min="0" [max]="bookHours" step="0.25" class="num mono" [(ngModel)]="bookOvertime"
              aria-describedby="bk-overtime-help" [attr.aria-invalid]="overtimeInvalid() ? 'true' : null" />
            <p id="bk-overtime-help" class="bo-hint">{{ i18n.t('mfg.records.overtimeHint') }}</p>
            @if (overtimeInvalid()) {
              <p class="notice warn" role="alert">{{ i18n.t('mfg.error.invalid_overtime_hours') }}</p>
            }
          </div>
          @if (canOverride() && overNeeded()) {
            <div class="bo-field">
              <label for="bk-why">{{ i18n.t('mfg.records.overrideReason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="bk-why" maxlength="400" [(ngModel)]="bookReason" />
              <p class="bo-hint">{{ i18n.t('mfg.records.overrideHint') }}</p>
            </div>
          } @else if (overNeeded()) {
            <p class="notice warn">{{ i18n.t('mfg.records.overAttendance') }}</p>
          }
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="bookingRow.set(null)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy() || !bookEmployee || overtimeInvalid()" (click)="book()">{{ i18n.t('common.save') }}</button>
      </div>
    </app-dialog>

    <app-dialog [open]="returning()" size="sm" [heading]="i18n.t('mfg.records.sendBack')" (dismiss)="returning.set(false)">
      @if (returning()) {
        <div class="editor bo-form">
          <div class="bo-field">
            <label for="rt-why">{{ i18n.t('mfg.records.whatToCorrect') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="rt-why" maxlength="400" [(ngModel)]="returnReason" />
          </div>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="returning.set(false)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy() || !returnReason.trim()" (click)="sendBack()">{{ i18n.t('mfg.records.sendBack') }}</button>
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
        grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
        margin: var(--sp-3) 0 0;
      }
      .facts dt {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .facts dd {
        margin: 0;
      }
      .row-card {
        display: grid;
        gap: var(--sp-2);
        padding-block: var(--sp-3);
        border-block-end: var(--line) solid var(--border);
      }
      .row-grid {
        display: grid;
        gap: var(--sp-2) var(--sp-3);
        grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr));
        align-items: end;
      }
      .row-wide {
        grid-column: 1 / -1;
      }
      .row-people {
        display: grid;
        gap: var(--sp-1);
        padding: var(--sp-2) var(--sp-3);
        background: var(--surface-2);
        border-radius: var(--radius-sm);
      }
      .row-title {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .booking {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2) var(--sp-3);
      }
      .row-remove {
        display: flex;
        justify-content: flex-end;
      }
      .editor {
        display: grid;
        gap: var(--sp-3);
      }
    `,
  ],
})
export class MfgRecordComponent implements OnInit {
  private readonly production = inject(ProductionApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly auth = inject(AuthService);
  private readonly regional = inject(RegionalService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly id = input.required<string>();

  readonly qty = qty;
  readonly categories = NON_ORDER_CATEGORIES;
  readonly log = signal<ProductionLog | null>(null);
  readonly orders = signal<ProductionOrderSummary[]>([]);
  readonly stages = signal<Record<string, OrderStage[]>>({});
  readonly candidates = signal<LogEmployeeCandidate[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly formError = signal<string | null>(null);
  readonly bookingRow = signal<ProductionLogRow | null>(null);
  readonly returning = signal(false);
  rows: RowDraft[] = [];
  notes = '';
  bookEmployee = '';
  bookHours = 1;
  /** C-05: how many of the hours were overtime; 0 by default. */
  bookOvertime = 0;
  bookReason = '';
  returnReason = '';

  readonly canOverride = computed(() => mfgCan(this.auth.currentUser(), MFG.labourOverride));

  async ngOnInit(): Promise<void> {
    try {
      const log = await this.production.log(this.id());
      this.show(log);
      const orders = await this.production.orders({ lineId: log.log.lineId });
      this.orders.set(orders);
      await Promise.all([...new Set(log.rows.map((r) => r.orderId).filter((x): x is string => !!x))].map((o) => this.loadStages(o)));
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.loading.set(false);
    }
  }

  private show(log: ProductionLog): void {
    this.log.set(log);
    this.rows = log.rows.map((r) => ({
      id: r.id, kind: r.orderId ? 'order' : 'other', orderId: r.orderId, stageId: r.stageId, category: r.category ?? 'Other',
      specification: r.specification ?? '', plannedQty: r.plannedQty, completedQty: r.completedQty, rejectedQty: r.rejectedQty,
      rejectReason: r.rejectReason ?? '', notes: r.notes ?? '',
    }));
    this.notes = log.notes ?? '';
    this.chrome?.heading.set(`${log.log.number} — ${log.log.lineCode} · ${this.day(log.log.day)}`);
    this.chrome?.description.set(null);
  }

  private async loadStages(orderId: string): Promise<void> {
    if (this.stages()[orderId]) return;
    const order = await this.production.order(orderId);
    this.stages.update((s) => ({ ...s, [orderId]: order.stages }));
  }

  /** The line's open orders, and the row's own even if it has closed since. */
  orderChoices(r: RowDraft): ProductionOrderSummary[] {
    return this.orders().filter((o) => o.status === 'Open' || o.id === r.orderId);
  }

  stagesOf(orderId: string | null): OrderStage[] {
    return orderId ? (this.stages()[orderId] ?? []) : [];
  }

  async orderChanged(r: RowDraft): Promise<void> {
    r.stageId = null;
    if (!r.orderId) return;
    await this.loadStages(r.orderId);
    r.stageId = this.stagesOf(r.orderId)[0]?.id ?? null;
  }

  stageName(s: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(s, this.i18n.lang());
  }

  day(value: string): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  tone(status: string) {
    return toneOf('productionLog', status);
  }

  warning(w: ProductionLogWarning): string {
    const lang = this.i18n.lang();
    const name = (ar: string, fr: string, en: string) => localizedName({ nameAr: ar, nameFr: fr, nameEn: en }, lang);
    return this.i18n.t('mfg.records.warning.' + w.code, {
      order: w.orderNumber, stage: name(w.stageNameAr, w.stageNameFr, w.stageNameEn), previous: name(w.previousNameAr, w.previousNameFr, w.previousNameEn),
      good: qty(w.stageGood), before: qty(w.previousGood),
    });
  }

  /** The saved row behind a draft row: hours and consumption hang on saved rows. */
  saved(r: RowDraft): ProductionLogRow | undefined {
    return r.id ? this.log()?.rows.find((x) => x.id === r.id) : undefined;
  }

  /** A row with hours or consumption keeps its operation (the server refuses otherwise). */
  used(r: RowDraft): boolean {
    const s = this.saved(r);
    return !!s && (s.bookings.length > 0 || s.consumptions.length > 0);
  }

  addRow(): void {
    const first = this.orders().find((o) => o.status === 'Open');
    const row: RowDraft = {
      id: null, kind: first ? 'order' : 'other', orderId: first?.id ?? null, stageId: null, category: 'Other',
      specification: '', plannedQty: 0, completedQty: 0, rejectedQty: 0, rejectReason: '', notes: '',
    };
    this.rows.push(row);
    if (first) void this.orderChanged(row);
  }

  private payload(): LogRowRequest[] {
    return this.rows.map((r) => ({
      id: r.id,
      orderId: r.kind === 'order' ? r.orderId : null,
      stageId: r.kind === 'order' ? r.stageId : null,
      category: r.kind === 'other' ? r.category : null,
      specification: r.kind === 'order' ? r.specification.trim() || null : null,
      plannedQty: Number(r.plannedQty) || 0,
      completedQty: r.kind === 'order' ? Number(r.completedQty) || 0 : 0,
      rejectedQty: r.kind === 'order' ? Number(r.rejectedQty) || 0 : 0,
      rejectReason: r.kind === 'order' && Number(r.rejectedQty) > 0 ? r.rejectReason.trim() || null : null,
      notes: r.notes.trim() || null,
    }));
  }

  private async run(work: () => Promise<ProductionLog>, done: string): Promise<boolean> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      this.show(await work());
      this.notice.set(this.i18n.t(done));
      return true;
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  async save(): Promise<boolean> {
    return this.run(() => this.production.saveLog(this.id(), this.notes.trim() || null, this.payload()), 'mfg.records.saved');
  }

  async submit(): Promise<void> {
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('mfg.records.submit'), this.i18n.t('mfg.records.submitWhat'))))) return;
    if (!(await this.save())) return;
    await this.run(() => this.production.submitLog(this.id()), 'mfg.records.submitted');
  }

  async approve(): Promise<void> {
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('mfg.records.approve'), this.i18n.t('mfg.records.approveWhat'))))) return;
    await this.run(() => this.production.approveLog(this.id()), 'mfg.records.approved');
  }

  async sendBack(): Promise<void> {
    const ok = await this.run(() => this.production.returnLog(this.id(), this.returnReason.trim()), 'mfg.records.sentBack');
    if (ok) {
      this.returning.set(false);
      this.returnReason = '';
    }
  }

  attendanceText(c: LogEmployeeCandidate): string {
    if (c.payrollLocked) return this.i18n.t('mfg.records.payrollLocked');
    if (c.attendanceHours === null) return this.i18n.t('mfg.records.attendanceDeclared');
    return this.i18n.t('mfg.records.attendanceOf', { n: qty(c.attendanceHours), booked: qty(c.bookedHours) });
  }

  /** The person picked in the hours dialog, with the day's attendance and what is booked already. */
  chosen(): LogEmployeeCandidate | undefined {
    return this.candidates().find((c) => c.employeeId === this.bookEmployee);
  }

  /** Whether these hours pass what the day's attendance leaves, so an override is needed. */
  overNeeded(): boolean {
    const c = this.candidates().find((x) => x.employeeId === this.bookEmployee);
    if (!c) return false;
    return c.attendanceHours === null || c.bookedHours + Number(this.bookHours) > c.attendanceHours;
  }

  /** Overtime hours are between 0 and the hours booked (the server refuses otherwise). */
  overtimeInvalid(): boolean {
    const overtime = Number(this.bookOvertime) || 0;
    return overtime < 0 || overtime > (Number(this.bookHours) || 0);
  }

  async openBooking(row: ProductionLogRow): Promise<void> {
    this.formError.set(null);
    this.bookHours = 1;
    this.bookOvertime = 0;
    this.bookReason = '';
    try {
      this.candidates.set(await this.production.candidates(this.id()));
      this.bookEmployee = this.candidates().find((c) => !c.payrollLocked && !row.bookings.some((b) => b.employeeId === c.employeeId))?.employeeId ?? '';
      this.bookingRow.set(row);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  async book(): Promise<void> {
    const row = this.bookingRow();
    if (!row) return;
    this.busy.set(true);
    this.formError.set(null);
    try {
      const reason = this.overNeeded() && this.canOverride() ? this.bookReason.trim() || null : null;
      this.show(await this.production.book(this.id(), row.id, this.bookEmployee, Number(this.bookHours), reason, Number(this.bookOvertime) || 0));
      this.bookingRow.set(null);
      this.notice.set(this.i18n.t('mfg.records.booked'));
    } catch (e) {
      this.formError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  async unbook(bookingId: string): Promise<void> {
    await this.run(() => this.production.unbook(this.id(), bookingId), 'mfg.records.saved');
  }
}
