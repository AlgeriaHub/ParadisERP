import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { saveBlob } from '../item-status/item-status-api.service';
import { costWords } from './cost-words';
import { MFG, mfgCan, mfgError, mfgSeesPay, qty } from './manufacturing-shared';
import { OrderCostReconciliation, ProductionApiService } from './production-api.service';

/**
 * An order's frozen cost (costing B2 — docs/manufacturing/costing-b2.md): B1's original snapshot, the late costs
 * recorded after it, the adjusted provisional total, and "financially finalized: not available" — never a zero.
 * With the reconciliation that explains them: elements and the rounding residual, releases, rejects, variances,
 * the integrity checks, and on request today's live calculation beside it. Read only: nothing here records, adjusts
 * or repairs anything.
 *
 * Drawn for a holder of `manufacturing.costs.view` when the server computes costs, once the order is completed.
 * A worker's rate, and the source of a payroll-derived late cost, come back only for whoever may see rates.
 */
@Component({
  selector: 'app-mfg-order-frozen-cost',
  standalone: true,
  imports: [FormsModule, DateInputComponent, StatusComponent],
  template: `
    @if (data(); as d) {
      @if (d.state !== 'not_completed') {
        <section class="bo-panel" aria-labelledby="mfg-frozen-title">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title" id="mfg-frozen-title">{{ i18n.t('mfg.b2.order.title') }}</h2>
            <app-status [tone]="tone('costHistory', d.state)">{{ i18n.t('mfg.b2.state.' + d.state) }}</app-status>
            @if (d.sheet; as h) {
              <app-status [tone]="tone('costSnapshotKind', h.kind)">{{ i18n.t('mfg.b2.kind.' + h.kind) }}</app-status>
              @if (h.sourceState !== 'complete') {
                <app-status [tone]="tone('costSource', h.sourceState)">{{ i18n.t('mfg.b2.source.' + h.sourceState) }}</app-status>
              }
            }
          </div>
          <div class="bo-panel__body">
            <div class="ui-toolbar frozen-tools">
              <div class="ui-filter">
                <label for="frozen-asof">{{ i18n.t('mfg.b2.asOf') }}</label>
                <app-date-input id="frozen-asof" [ngModel]="asOf()" (ngModelChange)="asOf.set($event || null); reload()" />
              </div>
              <label class="bo-check">
                <input type="checkbox" [ngModel]="live()" (ngModelChange)="live.set($event); reload()" />
                {{ i18n.t('mfg.b2.compareLive') }}
              </label>
              <div class="ui-toolbar__end">
                <button type="button" class="btn-ghost" [disabled]="exporting()" (click)="export()">{{ i18n.t('mfg.b2.export') }}</button>
              </div>
            </div>

            @if (d.state === 'not_frozen') {
              <p class="notice warn" role="status">{{ i18n.t('mfg.b2.order.notFrozen') }}</p>
            } @else if (d.state === 'not_frozen_at_date') {
              <p class="bo-hint">{{ i18n.t('mfg.b2.order.notFrozenAtDate') }}</p>
            }

            @if (d.sheet; as h) {
              <p class="bo-hint">
                {{ i18n.t('mfg.b2.order.frozenLine', { when: i18n.dateTime(h.frozenAtUtc), who: h.frozenBy || '—' }) }}
                @if (h.freezeLagDays) { · {{ i18n.t('mfg.b2.order.lag', { days: h.freezeLagDays }) }} }
                · {{ i18n.t('mfg.b2.finalized.notAvailable') }}
              </p>
            }

            @if (d.figures; as f) {
              <div class="ui-metrics">
                <div class="ui-metric">
                  <span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.original') }}</span>
                  <strong class="ui-metric__value mono">{{ i18n.money(f.original) }}</strong>
                </div>
                <div class="ui-metric">
                  <span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</span>
                  <strong class="ui-metric__value mono">{{ i18n.money(f.lateRecognized) }}</strong>
                </div>
                <div class="ui-metric ui-metric--lead">
                  <span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.adjusted') }}</span>
                  <strong class="ui-metric__value mono">{{ i18n.money(f.adjustedProvisional) }}</strong>
                </div>
                @if (f.lateUnclassified || f.lateUncertain) {
                  <div class="ui-metric ui-metric--warn">
                    <span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.notIncluded') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(f.lateUnclassified + f.lateUncertain) }}</strong>
                  </div>
                }
                <div class="ui-metric">
                  <span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.finalized') }}</span>
                  <strong class="ui-metric__value frozen-word">{{ i18n.t('mfg.b2.finalized.short') }}</strong>
                  <span class="ui-sub">{{ i18n.t('mfg.b2.finalized.why') }}</span>
                </div>
              </div>
            }
          </div>

          @if (d.figures) {
            <div class="bo-panel__body frozen-sub"><h3 class="frozen-subtitle">{{ i18n.t('mfg.b2.order.elements') }}</h3></div>
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('mfg.cost.element') }}</th>
                    <th>{{ i18n.t('common.state') }}</th>
                    <th class="num">{{ i18n.t('mfg.b2.fig.original') }}</th>
                    <th class="num">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</th>
                    <th class="num">{{ i18n.t('mfg.b2.fig.adjusted') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (e of d.elements; track e.element) {
                    <tr>
                      <td [attr.data-label]="i18n.t('mfg.cost.element')">{{ i18n.t('mfg.cost.el.' + e.element) }}</td>
                      <td [attr.data-label]="i18n.t('common.state')"><app-status [tone]="tone('costElement', e.status)">{{ i18n.t('mfg.cost.elementStatus.' + e.status) }}</app-status></td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.original')">{{ i18n.money(e.original) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.lateRecognized')">{{ e.lateRecognized ? i18n.money(e.lateRecognized) : '—' }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.adjusted')">{{ i18n.money(e.adjusted) }}</td>
                    </tr>
                  }
                  <tr class="frozen-total">
                    <td [attr.data-label]="i18n.t('mfg.cost.element')">{{ i18n.t('mfg.b2.rounding') }}</td>
                    <td></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.original')">{{ i18n.money(d.roundingResidual ?? 0) }}</td>
                    <td></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.adjusted')">{{ i18n.money(d.roundingResidual ?? 0) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
            <div class="bo-panel__body"><p class="bo-hint">{{ i18n.t('mfg.b2.order.equation') }}</p></div>
          }

          @if (d.lateCosts.length) {
            <div class="bo-panel__body frozen-sub"><h3 class="frozen-subtitle">{{ i18n.t('mfg.b2.tab.late') }}</h3></div>
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('mfg.b2.recordedAt') }}</th>
                    <th>{{ i18n.t('mfg.b2.kind') }}</th>
                    <th>{{ i18n.t('mfg.b2.treatment') }}</th>
                    <th class="num">{{ i18n.t('mfg.b2.recordedAmount') }}</th>
                    <th>{{ i18n.t('mfg.b2.sourceReason') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (l of d.lateCosts; track l.id) {
                    <tr>
                      <td [attr.data-label]="i18n.t('mfg.b2.recordedAt')">{{ i18n.dateTime(l.recordedAtUtc) }}</td>
                      <td [attr.data-label]="i18n.t('mfg.b2.kind')">
                        <span dir="ltr">{{ l.kind }}</span>
                        <span class="ui-sub">{{ l.element ? i18n.t('mfg.cost.el.' + l.element) : i18n.t('mfg.b2.treatmentWord.unclassified') }}</span>
                      </td>
                      <td [attr.data-label]="i18n.t('mfg.b2.treatment')">
                        <app-status [tone]="tone('lateTreatment', l.treatment)">{{ i18n.t('mfg.b2.treatmentWord.' + l.treatment) }}</app-status>
                        @for (fl of l.flags; track fl) { <span class="ui-sub">{{ i18n.t('mfg.b2.flag.' + fl) }}</span> }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.recordedAmount')">{{ i18n.money(l.recordedAmount) }}</td>
                      <td [attr.data-label]="i18n.t('mfg.b2.sourceReason')">
                        @if (l.redacted) {
                          <span class="ui-sub">{{ i18n.t('mfg.b2.withheld') }}</span>
                        } @else {
                          <span dir="ltr">{{ l.sourceReference }}</span>
                          <span class="ui-sub">{{ l.reason }}</span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }

          @if (d.releases; as r) {
            <div class="bo-panel__body frozen-sub">
              <h3 class="frozen-subtitle">{{ i18n.t('mfg.b2.tab.releases') }}</h3>
              <p class="bo-hint">
                {{ i18n.t('mfg.b2.rel.summary', { released: qty(r.releasedQty), accepted: qty(r.acceptedQty), unit: d.baseUnitCode }) }}
                @if (r.keptInCharges) { · {{ i18n.t('mfg.b2.rel.kept', { amount: i18n.money(r.keptInCharges) }) }} }
                @if (r.lateNotAllocated) { · {{ i18n.t('mfg.b2.rel.lateNotAllocated', { amount: i18n.money(r.lateNotAllocated) }) }} }
              </p>
              @for (fl of r.flags; track fl) { <p class="notice warn" role="status">{{ i18n.t('mfg.b2.flag.' + fl, { qty: qty(r.unreleasedAcceptedQty) }) }}</p> }
            </div>
            @if (r.rows.length) {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('mfg.orders.number') }}</th>
                      <th>{{ i18n.t('mfg.b2.releasedAt') }}</th>
                      <th class="num">{{ i18n.t('mfg.b2.quantity') }}</th>
                      <th class="num">{{ i18n.t('mfg.b2.value') }}</th>
                      <th class="num">{{ i18n.t('mfg.b2.unitValue') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (x of r.rows; track x.releaseId) {
                      <tr>
                        <td [attr.data-label]="i18n.t('mfg.orders.number')">
                          <span dir="ltr">{{ x.number }}</span>
                          @if (x.carriesRemainder) { <span class="ui-sub">{{ i18n.t('mfg.b2.rel.carries') }}</span> }
                        </td>
                        <td [attr.data-label]="i18n.t('mfg.b2.releasedAt')">{{ i18n.dateTime(x.atUtc) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.quantity')">{{ qty(x.quantity) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.value')">{{ i18n.money(x.value) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.unitValue')">{{ i18n.money(x.unitValue) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          }

          @if (d.scrap; as sc) {
            <div class="bo-panel__body frozen-sub">
              <h3 class="frozen-subtitle">{{ i18n.t('mfg.b2.scrap.title') }}</h3>
              <p class="bo-hint">
                {{ i18n.t('mfg.b2.scrap.rejected', { rejected: qty(sc.rejectedQty), unit: d.baseUnitCode, rate: sc.rejectionRatePercent ?? '—' }) }}
                @if (sc.spoilageAbsorbed !== null) { · {{ i18n.t('mfg.b2.scrap.spoilage', { amount: i18n.money(sc.spoilageAbsorbed) }) }} }
                @if (sc.materialScrapValue !== null) { · {{ i18n.t('mfg.b2.scrap.material', { amount: i18n.money(sc.materialScrapValue) }) }} }
                · {{ i18n.t('mfg.b2.scrap.recovery') }}: {{ i18n.t('mfg.cost.elementStatus.' + sc.recoveryStatus) }}
              </p>
            </div>
          }

          @if (d.variances; as v) {
            <div class="bo-panel__body frozen-sub"><h3 class="frozen-subtitle">{{ i18n.t('mfg.cost.variances') }}</h3></div>
            @if (v.available) {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('mfg.b2.component') }}</th>
                      <th class="num">{{ i18n.t('mfg.b2.fig.original') }}</th>
                      <th class="num">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</th>
                      <th class="num">{{ i18n.t('mfg.b2.fig.adjusted') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (l of v.lines; track l.component) {
                      <tr [class.frozen-total]="l.component === 'total'" [class.frozen-detail]="l.detail">
                        <td [attr.data-label]="i18n.t('mfg.b2.component')">{{ i18n.t('mfg.b2.var.' + l.component) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.original')">{{ money(l.original) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.lateRecognized')">{{ money(l.lateRecognized) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.adjusted')">{{ money(l.adjusted) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <div class="bo-panel__body"><p class="bo-hint">{{ i18n.t('mfg.b2.unavailable.' + (v.reason ?? 'unknown')) }}</p></div>
            }
          }

          @if (d.labour; as lab) {
            @if (lab.rows.length) {
              <div class="bo-panel__body frozen-sub">
                <h3 class="frozen-subtitle">{{ i18n.t('mfg.cost.el.labour') }}</h3>
                @if (lab.hoursWithheld) {
                  <p class="bo-hint">{{ i18n.t('mfg.b2.labour.hoursWithheld') }}</p>
                } @else if (!lab.perEmployeeMoneyShown) {
                  <p class="bo-hint">{{ i18n.t('mfg.b2.labour.moneyWithheld') }}</p>
                }
              </div>
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('mfg.b2.employee') }}</th>
                      <th>{{ i18n.t('mfg.b2.month') }}</th>
                      <th class="num">{{ i18n.t('mfg.b2.hours') }}</th>
                      @if (lab.perEmployeeMoneyShown) {
                        <th class="num">{{ i18n.t('mfg.b2.rate') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.value') }}</th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (x of lab.rows; track x.employeeNumber + x.year + x.month) {
                      <tr>
                        <td [attr.data-label]="i18n.t('mfg.b2.employee')">{{ x.employeeName }} <span class="ui-sub" dir="ltr">{{ x.employeeNumber }}</span></td>
                        <td class="mono" [attr.data-label]="i18n.t('mfg.b2.month')" dir="ltr">{{ x.month.toString().padStart(2, '0') }}/{{ x.year }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.hours')">{{ x.hours === null ? i18n.t('mfg.b2.withheld') : qty(x.hours) }}</td>
                        @if (lab.perEmployeeMoneyShown) {
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.rate')">{{ money(x.rate) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.value')">{{ money(x.cost) }}</td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          }

          @if (d.live; as lv) {
            <div class="bo-panel__body frozen-sub">
              <h3 class="frozen-subtitle">{{ i18n.t('mfg.b2.live.title') }}</h3>
              <p class="bo-hint">{{ i18n.t('mfg.b2.live.lead') }}</p>
              @if (!lv.comparable) {
                <p class="notice warn" role="status">{{ i18n.t('mfg.b2.live.notComparable.' + (lv.reason ?? 'unknown')) }}</p>
              } @else {
                <div class="ui-metrics">
                  <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.adjusted') }}</span><strong class="ui-metric__value mono">{{ i18n.money(lv.adjustedProvisional) }}</strong></div>
                  <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.live') }}</span><strong class="ui-metric__value mono">{{ money(lv.live) }}</strong></div>
                  <div class="ui-metric ui-metric--lead" [class.ui-metric--warn]="lv.unexplained"><span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.unexplained') }}</span><strong class="ui-metric__value mono">{{ money(lv.unexplained) }}</strong></div>
                </div>
                @if (lv.lateRecognizedAfterAsOf) {
                  <p class="bo-hint">{{ i18n.t('mfg.b2.live.afterAsOf', { amount: i18n.money(lv.lateRecognizedAfterAsOf) }) }}</p>
                }
                @for (c of lv.sourceChanges; track c.code) {
                  <p class="bo-hint">{{ words.change(c) }}</p>
                }
              }
            </div>
          }

          @if (d.checks.length) {
            <div class="bo-panel__body frozen-sub">
              <h3 class="frozen-subtitle">{{ i18n.t('mfg.b2.tab.integrity') }}</h3>
              <ul class="frozen-checks">
                @for (c of d.checks; track c.code) {
                  <li>
                    <app-status [tone]="tone('costCheck', c.result)">{{ i18n.t('mfg.b2.check.result.' + c.result) }}</app-status>
                    <span>{{ i18n.t('mfg.b2.check.' + c.code) }}</span>
                  </li>
                }
              </ul>
              @for (g of d.gaps; track g.code + (g.element ?? '') + (g.detail ?? '')) {
                <p class="bo-hint">{{ words.gap(g) }}</p>
              }
            </div>
          }

          <div class="bo-panel__body frozen-notes">
            @for (l of d.limitations; track l) {
              <p class="bo-hint">{{ i18n.t('mfg.b2.limit.' + l) }}</p>
            }
          </div>
        </section>
      }
    } @else if (payWithheld()) {
      <p class="bo-hint">{{ i18n.t('mfg.b2.payWithheld') }}</p>
    } @else if (error()) {
      <p class="notice danger" role="alert">{{ error() }}</p>
    }
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .frozen-tools {
        border: 0;
        padding: 0;
      }
      .frozen-word {
        font-size: var(--fs-section);
      }
      .frozen-sub {
        padding-block-end: 0;
      }
      .frozen-subtitle {
        margin: 0;
        font-size: var(--fs-section);
        font-weight: 600;
      }
      .frozen-total td {
        background: var(--surface-2);
      }
      .frozen-detail td:first-child {
        padding-inline-start: var(--sp-5);
        color: var(--muted);
      }
      .frozen-checks {
        display: grid;
        gap: var(--sp-1);
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .frozen-checks li {
        display: flex;
        align-items: center;
        gap: var(--sp-2);
      }
      .frozen-notes {
        gap: var(--sp-1);
        border-block-start: var(--line) solid var(--border);
      }
    `,
  ],
})
export class MfgOrderFrozenCostComponent {
  private readonly production = inject(ProductionApiService);
  private readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);
  readonly words = costWords(this.i18n);

  readonly orderId = input.required<string>();
  /** Bumped by the order page after each change. */
  readonly version = input(0);

  readonly qty = qty;
  readonly data = signal<OrderCostReconciliation | null>(null);
  readonly error = signal<string | null>(null);
  readonly asOf = signal<string | null>(null);
  readonly live = signal(false);
  readonly exporting = signal(false);
  /** The reader may see costs but not pay: the frozen cost is labour by order, so it is not loaded, and the panel says why. */
  readonly payWithheld = signal(false);
  private enabled: Promise<boolean> | null = null;

  readonly frozen = computed(() => this.data()?.state === 'frozen');

  constructor() {
    effect(() => {
      this.orderId();
      this.version();
      untracked(() => void this.reload());
    });
  }

  async reload(): Promise<void> {
    if (!mfgCan(this.auth.currentUser(), MFG.costsView)) return;
    this.enabled ??= this.production.operationsStatus().then((s) => s.costingEnabled, () => false);
    if (!(await this.enabled)) return;
    this.payWithheld.set(!mfgSeesPay(this.auth.currentUser()));
    if (this.payWithheld()) return;
    try {
      this.data.set(await this.production.costReconciliation(this.orderId(), this.asOf(), this.live()));
      this.error.set(null);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  async export(): Promise<void> {
    const d = this.data();
    if (!d) return;
    this.exporting.set(true);
    try {
      saveBlob(await this.production.exportOrderCost(d.orderId, this.asOf(), this.live(), this.i18n.lang()), `manufacturing-order-${d.orderNumber}.xlsx`);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.exporting.set(false);
    }
  }

  tone(family: 'costHistory' | 'costSnapshotKind' | 'costSource' | 'costCheck' | 'lateTreatment' | 'costElement', status: string) {
    return toneOf(family, status);
  }

  /** A figure or a dash: an absent value is never shown as zero. */
  money(value: number | null): string {
    return value === null ? '—' : this.i18n.money(value);
  }
}
