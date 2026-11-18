import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { MFG, mfgCan, mfgError, qty } from './manufacturing-shared';
import {
  CostElementStatus, CostElementStatuses, CostElements, CostVariances, ProductionApiService, ProductionCostSheet,
} from './production-api.service';

/** The cost notes that need someone to act: shown as warnings; the others explain and are hints. */
const WARNINGS = new Set(['labour_unvalued', 'material_unvalued', 'final_release_below_zero']);

const ELEMENTS: (keyof CostElementStatuses)[] = ['materials', 'consumables', 'accessories', 'labour', 'overhead', 'expenses', 'recovery'];

/** The labour notes count hours; when the hours are withheld they come without the figure, in other words. */
const HOUR_NOTES = new Set(['labour_standard_rate', 'labour_unvalued', 'labour_overtime_without_premium']);

type VarianceKey = keyof CostVariances;
const SPLIT: VarianceKey[] = ['materialUsage', 'materialPrice', 'labourEfficiency', 'labourRate', 'overhead', 'expenses', 'recovery', 'total'];
const TOGETHER: VarianceKey[] = ['materialUsage', 'materialPrice', 'labour', 'overhead', 'expenses', 'recovery', 'total'];

/**
 * An order's cost sheet (costing, Phase A — docs/manufacturing/costing-plan.md): estimated against actual
 * by element, cost per accepted unit, each material, labour per employee and payroll month, the releases
 * and the variances. Computed by the server on every read from what was recorded; nothing is frozen or
 * posted, and every figure that is not final says why, in words.
 *
 * Drawn only when this server computes costs and the reader holds `manufacturing.costs.view`. An
 * employee's rate and pay come back only for a holder of `payroll.manage` and the rates key; for anyone
 * else the column is left out and the order's totals remain. Nor does that reader get what divides back
 * to one person's rate — the hours of an order one employee worked, a labour estimate resting on one
 * employee's rate, the labour variance split — and each such figure reads "withheld", never a blank.
 */
@Component({
  selector: 'app-mfg-order-costing',
  standalone: true,
  imports: [StatusComponent],
  template: `
    @if (sheet(); as s) {
      <section class="bo-panel" aria-labelledby="mfg-cost-title">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title" id="mfg-cost-title">{{ i18n.t('mfg.cost.title') }}</h2>
          <app-status [tone]="sheetTone(s.costStatus)">{{ i18n.t('mfg.cost.status.' + s.costStatus) }}</app-status>
        </div>
        <div class="bo-panel__body">
          <p class="bo-hint">{{ i18n.t('mfg.cost.lead') }}</p>
          @for (n of warnings(); track n.code) {
            <p class="notice warn" role="status">{{ note(n.code, n.value) }}</p>
          }
          @if (s.orderMoneyWithheld) {
            <p class="notice info">{{ i18n.t('mfg.cost.orderMoneyWithheld') }}</p>
          } @else if (s.labourEstimateWithheld) {
            <p class="bo-hint">{{ i18n.t('mfg.cost.estimateWithheld') }}</p>
          }
          <div class="ui-metrics">
            @if (s.actualUnitCost !== null) {
              <div class="ui-metric ui-metric--lead">
                <span class="ui-metric__label">{{ i18n.t('mfg.cost.actualUnit.' + s.costStatus, { unit: s.baseUnitCode }) }}</span>
                <strong class="ui-metric__value mono">{{ i18n.money(s.actualUnitCost) }}</strong>
              </div>
            }
            <div class="ui-metric" [class.ui-metric--lead]="s.actualUnitCost === null">
              <span class="ui-metric__label">{{ i18n.t((s.completed ? 'mfg.cost.completed.' : 'mfg.cost.toDate.') + s.costStatus) }}</span>
              <strong class="ui-metric__value mono">{{ amount(s.actual.total) }}</strong>
            </div>
            <div class="ui-metric">
              <span class="ui-metric__label">{{ i18n.t('mfg.cost.estimatedUnit', { unit: s.baseUnitCode }) }}</span>
              <strong class="ui-metric__value mono">{{ s.labourEstimateWithheld ? i18n.t('mfg.cost.withheld') : s.estimatedUnitCost === null ? '—' : i18n.money(s.estimatedUnitCost) }}</strong>
            </div>
            <div class="ui-metric">
              <span class="ui-metric__label">{{ i18n.t('mfg.cost.released') }}</span>
              <strong class="ui-metric__value mono">{{ amount(s.releasedValue) }}</strong>
            </div>
            @if (!s.completed) {
              <div class="ui-metric">
                <span class="ui-metric__label">{{ i18n.t('mfg.cost.wip') }}</span>
                <strong class="ui-metric__value mono">{{ amount(s.workInProgress) }}</strong>
              </div>
            } @else if (s.keptInCharges) {
              <div class="ui-metric">
                <span class="ui-metric__label">{{ i18n.t('mfg.cost.keptInCharges') }}</span>
                <strong class="ui-metric__value mono">{{ amount(s.keptInCharges) }}</strong>
              </div>
            }
          </div>
        </div>

        <div class="ui-scroll">
          <table class="ui-table ui-table--cards">
            <caption class="sr-only">{{ i18n.t('mfg.cost.byElement') }}</caption>
            <thead>
              <tr>
                <th>{{ i18n.t('mfg.cost.element') }}</th>
                <th class="num">{{ i18n.t('mfg.cost.estimate') }}</th>
                <th class="num">{{ i18n.t('mfg.cost.actualCol.' + s.costStatus) }}</th>
                <th>{{ i18n.t('common.state') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (e of elements; track e) {
                <tr>
                  <td [attr.data-label]="i18n.t('mfg.cost.element')">
                    {{ i18n.t('mfg.cost.el.' + e) }}
                    @if (e === 'labour' && s.employerSocialMissing) {
                      <span class="ui-sub">{{ i18n.t('mfg.cost.withoutEmployerSocial') }}</span>
                    }
                  </td>
                  <td class="num" [attr.data-label]="i18n.t('mfg.cost.estimate')">{{ estimateCell(s, e) }}</td>
                  <td class="num" [attr.data-label]="i18n.t('mfg.cost.actualCol.' + s.costStatus)">{{ actualCell(s, e) }}</td>
                  <td [attr.data-label]="i18n.t('common.state')">
                    <app-status [tone]="elementTone(s.statuses[e])">{{ i18n.t('mfg.cost.elementStatus.' + s.statuses[e]) }}</app-status>
                  </td>
                </tr>
              }
              <tr class="cost-total">
                <td [attr.data-label]="i18n.t('mfg.cost.element')"><strong>{{ i18n.t('mfg.cost.total') }}</strong></td>
                <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.estimate')"><strong>{{ s.estimate.total === null ? i18n.t('mfg.cost.withheld') : i18n.money(s.estimate.total) }}</strong></td>
                <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.actualCol.' + s.costStatus)"><strong>{{ amount(s.actual.total) }}</strong></td>
                <td [attr.data-label]="i18n.t('common.state')">
                  <app-status [tone]="sheetTone(s.costStatus)">{{ i18n.t('mfg.cost.status.' + s.costStatus) }}</app-status>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        @if (s.materials.length) {
          <div class="bo-panel__body cost-sub"><h3 class="cost-subtitle">{{ i18n.t('mfg.orders.materials') }}</h3></div>
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.bom.component', { n: '' }) }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.netQty') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.netValue') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.consumedValue') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.scrapValue') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.atLine') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.usageVariance') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.priceVariance') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (m of s.materials; track m.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfg.bom.component', { n: '' })">
                      <span class="mono" dir="ltr">{{ m.sku }}</span> <span class="ui-sub">{{ i18n.t('mfg.role.' + m.role) }}</span>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.netQty')"><span dir="ltr">{{ qty(m.netBaseQty) }} {{ m.baseUnitCode }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.netValue')">{{ i18n.money(m.netValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.consumedValue')">{{ i18n.money(m.consumedValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.scrapValue')">{{ i18n.money(m.scrapValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.atLine')">{{ i18n.money(m.atLineValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.usageVariance')">{{ variance(m.usageVariance) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.priceVariance')">{{ variance(m.priceVariance) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        @if (s.labour.length) {
          <div class="bo-panel__body cost-sub">
            <h3 class="cost-subtitle">{{ i18n.t('mfg.cost.labour') }}</h3>
            @if (!s.perEmployeeMoneyShown) {
              <p class="bo-hint">{{ i18n.t('mfg.cost.moneyHidden') }}</p>
            }
            @if (s.labourHoursWithheld) {
              <p class="bo-hint">{{ i18n.t('mfg.cost.hoursWithheld') }}</p>
            }
          </div>
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.records.employee') }}</th>
                  <th>{{ i18n.t('mfg.cost.payrollMonth') }}</th>
                  <th class="num">{{ i18n.t('mfg.records.hours') }}</th>
                  <th>{{ i18n.t('mfg.cost.basis') }}</th>
                  @if (s.perEmployeeMoneyShown) {
                    <th class="num">{{ i18n.t('mfg.cost.rate') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.amount') }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (l of s.labour; track l.employeeId + '-' + l.year + '-' + l.month) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfg.records.employee')">{{ l.employeeName }} <span class="ui-sub mono" dir="ltr">{{ l.employeeNumber }}</span></td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.cost.payrollMonth')"><span dir="ltr">{{ l.year }}-{{ pad(l.month) }}</span></td>
                    <td class="num" [attr.data-label]="i18n.t('mfg.records.hours')">
                      <span class="mono">{{ l.hours === null ? i18n.t('mfg.cost.withheld') : qty(l.hours) }}</span>
                      @if (l.overtimeHours) {
                        <span class="ui-sub">{{ i18n.t('mfg.records.overtimeOf', { n: qty(l.overtimeHours) }) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.cost.basis')">
                      {{ i18n.t('mfg.cost.basisWord.' + l.basis) }}
                      @if (l.reason) {
                        <span class="ui-sub">{{ i18n.t('mfg.cost.reason.' + l.reason) }}</span>
                      }
                      @if (l.withoutEmployerSocial) {
                        <span class="ui-sub">{{ i18n.t('mfg.cost.withoutEmployerSocial') }}</span>
                      }
                    </td>
                    @if (s.perEmployeeMoneyShown) {
                      <td class="num" [attr.data-label]="i18n.t('mfg.cost.rate')">
                        <span class="mono">{{ l.rate === null ? '—' : i18n.money(l.rate) }}</span>
                        @if (l.overtimeHours && l.overtimeRate !== null && l.overtimeRate !== undefined && l.overtimeRate !== l.rate) {
                          <span class="ui-sub">{{ i18n.t('mfg.cost.overtimeRate', { value: i18n.money(l.overtimeRate) }) }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.amount')">{{ l.cost === null ? '—' : i18n.money(l.cost) }}</td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        @if (s.releases.length) {
          <div class="bo-panel__body cost-sub"><h3 class="cost-subtitle">{{ i18n.t('mfg.cost.releases') }}</h3></div>
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.orders.number') }}</th>
                  <th class="num">{{ i18n.t('mfg.reports.quantity') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.unitValue') }}</th>
                  <th class="num">{{ i18n.t('mfg.reports.value') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (r of s.releases; track r.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.orders.number')">
                      <span dir="ltr">{{ r.number }}</span>
                      @if (r.carriesRemainder) {
                        <span class="ui-sub">{{ i18n.t('mfg.cost.carriesRemainder') }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.reports.quantity')"><span dir="ltr">{{ qty(r.baseQty) }} {{ s.baseUnitCode }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.unitValue')">{{ amount(r.unitValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.reports.value')">{{ amount(r.value) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        @if (s.variances; as v) {
          <div class="bo-panel__body cost-sub">
            <h3 class="cost-subtitle">{{ i18n.t('mfg.cost.variances') }}</h3>
            <p class="bo-hint">{{ i18n.t('mfg.cost.variancesHint', { hours: qty(s.standardHoursForOutput), unit: s.baseUnitCode, accepted: qty(s.acceptedBaseQty) }) }}</p>
          </div>
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.cost.variance') }}</th>
                  <th class="num">{{ i18n.t('mfg.reports.value') }}</th>
                  <th>{{ i18n.t('mfg.cost.direction') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (key of varianceKeys(v); track key) {
                  <tr [class.cost-total]="key === 'total'">
                    <td [attr.data-label]="i18n.t('mfg.cost.variance')">{{ i18n.t('mfg.cost.var.' + key) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.reports.value')">{{ varianceCell(v[key]) }}</td>
                    <td [attr.data-label]="i18n.t('mfg.cost.direction')">{{ direction(v[key]) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (s.spoilage !== null) {
            <div class="bo-panel__body"><p class="bo-hint">{{ i18n.t('mfg.cost.spoilage', { amount: i18n.money(s.spoilage), rejected: qty(s.rejectedBaseQty), unit: s.baseUnitCode }) }}</p></div>
          }
        }

        @if (hints().length) {
          <div class="bo-panel__body cost-notes">
            @for (n of hints(); track n.code) {
              <p class="bo-hint">{{ note(n.code, n.value) }}</p>
            }
          </div>
        }
      </section>
    } @else if (error()) {
      <p class="notice danger" role="alert">{{ error() }}</p>
    }
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .cost-sub {
        padding-block-end: 0;
      }
      .cost-subtitle {
        margin: 0;
        font-size: var(--fs-section);
        font-weight: 600;
      }
      .cost-total td {
        background: var(--surface-2);
      }
      .cost-notes {
        gap: var(--sp-1);
        border-block-start: var(--line) solid var(--border);
      }
    `,
  ],
})
export class MfgOrderCostingComponent {
  private readonly production = inject(ProductionApiService);
  private readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);

  readonly orderId = input.required<string>();
  /** Bumped by the order page after each change, so the sheet is read again. */
  readonly version = input(0);

  readonly qty = qty;
  readonly elements = ELEMENTS;
  readonly sheet = signal<ProductionCostSheet | null>(null);
  readonly error = signal<string | null>(null);
  private enabled: Promise<boolean> | null = null;

  readonly warnings = computed(() => (this.sheet()?.notes ?? []).filter((n) => WARNINGS.has(n.code)));
  readonly hints = computed(() => (this.sheet()?.notes ?? []).filter((n) => !WARNINGS.has(n.code)));

  constructor() {
    effect(() => {
      const id = this.orderId();
      this.version();
      untracked(() => void this.load(id));
    });
  }

  private async load(id: string): Promise<void> {
    if (!mfgCan(this.auth.currentUser(), MFG.costsView)) return;
    this.enabled ??= this.production.operationsStatus().then((s) => s.costingEnabled, () => false);
    if (!(await this.enabled)) return;
    try {
      this.sheet.set(await this.production.costing(id));
      this.error.set(null);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  /** Scrap recovery reduces the cost, so it reads with a minus; a figure withheld from this reader says so. */
  signed(element: keyof CostElements, value: number | null): string {
    if (value === null) return this.i18n.t('mfg.cost.withheld');
    return element === 'recovery' && value ? `− ${this.i18n.money(value)}` : this.i18n.money(value);
  }

  /** An amount, or the word for one withheld from this reader — never a blank someone could take for zero. */
  amount(value: number | null): string {
    return value === null ? this.i18n.t('mfg.cost.withheld') : this.i18n.money(value);
  }

  /**
   * An actual figure, or the word for what it is not: nothing to cost reads "—", and a rate nobody
   * approved or a recovery not recorded reads as such — never as a zero someone could take for a cost.
   */
  actualCell(s: ProductionCostSheet, element: keyof CostElementStatuses): string {
    const status = s.statuses[element];
    if (status === 'none') return '—';
    if (status === 'not_set' || status === 'not_recorded') return this.i18n.t('mfg.cost.elementStatus.' + status);
    return this.signed(element, s.actual[element]);
  }

  /**
   * The estimate's figure; an overhead with no approved rate, or labour with no standard rate, has none,
   * and labour withheld from this reader says so.
   */
  estimateCell(s: ProductionCostSheet, element: keyof CostElementStatuses): string {
    if (element === 'overhead' && s.statuses.overhead === 'not_set') return this.i18n.t('mfg.cost.elementStatus.not_set');
    if (element === 'labour' && s.notes.some((n) => n.code === 'labour_estimate_unavailable')) return this.i18n.t('mfg.cost.unavailable');
    const value = s.estimate[element];
    if (value === null) return this.i18n.t('mfg.cost.withheld');
    if ((element === 'expenses' || element === 'recovery') && value === 0) return '—';
    return this.signed(element, value);
  }

  /** A variance's figure, or the word for one withheld from this reader. */
  varianceCell(value: number | null): string {
    return value === null ? this.i18n.t('mfg.cost.withheld') : this.i18n.money(value);
  }

  /** Labour split into efficiency and rate when the server sent the split, else as one line. */
  varianceKeys(v: CostVariances): VarianceKey[] {
    return v.labourEfficiency === null ? TOGETHER : SPLIT;
  }

  sheetTone(status: string) {
    return toneOf('costSheet', status);
  }

  elementTone(status: CostElementStatus) {
    return toneOf('costElement', status);
  }

  variance(value: number | null): string {
    return value === null ? '—' : this.i18n.money(value);
  }

  /** A variance in words beside its sign: above the standard is unfavourable, below it favourable. */
  direction(value: number | null): string {
    if (value === null || Math.abs(value) < 0.0005) return '—';
    return this.i18n.t(value > 0 ? 'mfg.cost.unfavourable' : 'mfg.cost.favourable');
  }

  note(code: string, value: number | null): string {
    if (value === null && (HOUR_NOTES.has(code) || code === 'final_release_below_zero')) return this.i18n.t('mfg.cost.note.' + code + '_withheld');
    const money = code === 'final_release_below_zero' || code === 'expense_unsettled';
    const amount = value === null ? '' : money ? this.i18n.money(value) : qty(value);
    return this.i18n.t('mfg.cost.note.' + code, { value: amount });
  }

  pad(month: number): string {
    return String(month).padStart(2, '0');
  }
}
