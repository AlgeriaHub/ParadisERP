import { Component, OnInit, inject, input, signal } from '@angular/core';
import { I18nService } from '../../../core/i18n.service';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import { MaintenanceJob } from './maintenance-api.service';
import { MaintenanceCostApi, MaintenanceJobCost } from './maintenance-cost-api.service';
import { costNote } from './maintenance-cost-words';
import { maintenanceError } from './maintenance-words';

/**
 * Maintenance › Job › Cost (Phase M4, docs/maintenance/plan.md §3.1, §6.2 and §7): what the job cost, computed on read —
 * parts at the values their issues were frozen at, labour at payroll's rates (standard until the month's payroll is
 * approved, actual after) or the one workshop rate, outside work, and overhead per labour hour when MS-10 includes it. It
 * says in words whether the figure is final or provisional and why. Without pay-rate access the reader sees the hours, the
 * parts and the outside work but not the labour money or the total (the server leaves them out). Shown to holders of
 * `maintenance.costs.view`; a working card, never folded.
 */
@Component({
  selector: 'app-maintenance-job-cost',
  standalone: true,
  imports: [StatusComponent, TableStateComponent],
  template: `
    <section class="ui-surface" aria-labelledby="mj-cost-title">
      <div class="ui-surface__head">
        <h2 id="mj-cost-title">{{ i18n.t('maint.cost.title') }}</h2>
        @if (cost(); as c) {
          <app-status [tone]="sheetTone(c.status)">{{ i18n.t('maint.cost.status.' + c.status) }}</app-status>
        }
      </div>
      <div class="ui-surface__body cost">
        @if (loading() && !cost()) {
          <app-table-state kind="loading" />
        } @else if (error() && !cost()) {
          <app-table-state kind="error" [message]="error()" (retry)="load()" />
        } @else if (cost(); as c) {
          <p class="bo-hint">{{ basis(c) }}</p>
          @if (!c.labourMoneyShown) {
            <p class="notice info" role="status">{{ i18n.t('maint.cost.withheld') }}</p>
          }

          <div class="ui-metrics">
            <div class="ui-metric ui-metric--lead">
              <span class="ui-metric__label">{{ i18n.t('maint.cost.total.' + c.status) }}</span>
              @if (c.total !== null) {
                <strong class="ui-metric__value mono">{{ i18n.money(c.total) }}</strong>
              } @else {
                <strong class="ui-metric__value">{{ i18n.t('maint.cost.notShown') }}</strong>
              }
            </div>
            <div class="ui-metric">
              <span class="ui-metric__label">{{ i18n.t('maint.cost.parts') }}</span>
              <strong class="ui-metric__value mono">{{ i18n.money(c.parts) }}</strong>
            </div>
            <div class="ui-metric">
              <span class="ui-metric__label">{{ i18n.t('maint.cost.labour') }}</span>
              @if (c.statuses.labour === 'excluded') {
                <strong class="ui-metric__value">{{ i18n.t('maint.cost.excludedShort') }}</strong>
              } @else if (c.labour !== null) {
                <strong class="ui-metric__value mono">{{ i18n.money(c.labour) }}</strong>
              } @else {
                <strong class="ui-metric__value">{{ i18n.t('maint.cost.notShown') }}</strong>
              }
              <span class="ui-sub">{{ i18n.t('maint.cost.hours', { n: qty(c.labourHours) }) }}</span>
            </div>
            <div class="ui-metric">
              <span class="ui-metric__label">{{ i18n.t('maint.cost.outsideWork') }}</span>
              <strong class="ui-metric__value mono">{{ i18n.money(c.outsideWork) }}</strong>
            </div>
            @if (c.statuses.overhead !== 'excluded') {
              <div class="ui-metric">
                <span class="ui-metric__label">{{ i18n.t('maint.cost.overhead') }}</span>
                <strong class="ui-metric__value mono">{{ i18n.money(c.overhead) }}</strong>
                <span class="ui-sub">{{ i18n.t('maint.cost.overheadRate', { rate: i18n.money(c.overheadPerLabourHour) }) }}</span>
              </div>
            }
            @if (c.recharge !== null) {
              <div class="ui-metric">
                <span class="ui-metric__label">{{ i18n.t('maint.cost.recharge') }}</span>
                <strong class="ui-metric__value mono">{{ i18n.money(c.recharge) }}</strong>
                @if (c.rechargeLessCost !== null) {
                  <span class="ui-sub">{{ i18n.t('maint.cost.rechargeLessCost', { amount: i18n.money(c.rechargeLessCost) }) }}</span>
                }
              </div>
            }
          </div>
          <!-- Phase M7d (MS-30): a repair capitalised at completion joined its equipment's register pool; said in words. -->
          @if (c.capitalisedAtUtc) {
            <p class="notice info" role="status">
              {{ c.capitalised !== null && c.capitalised !== undefined
                ? i18n.t('maint.cost.capitalised', { amount: i18n.money(c.capitalised), date: i18n.date(c.capitalisedAtUtc) })
                : i18n.t('maint.cost.capitalisedWithheld', { date: i18n.date(c.capitalisedAtUtc) }) }}
            </p>
          }

          <div class="ui-scroll">
            <table class="ui-table ui-table--cards elements">
              <caption class="sr-only">{{ i18n.t('maint.cost.elements') }}</caption>
              <thead>
                <tr>
                  <th>{{ i18n.t('maint.cost.element') }}</th>
                  <th class="num">{{ i18n.t('maint.cost.amount') }}</th>
                  <th>{{ i18n.t('maint.cost.restsOn') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (e of elements(c); track e.key) {
                  <tr>
                    <td [attr.data-label]="i18n.t('maint.cost.element')">{{ i18n.t('maint.cost.' + e.key) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.cost.amount')">
                      @if (e.status === 'excluded') {
                        —
                      } @else if (e.amount !== null) {
                        {{ i18n.money(e.amount) }}
                      } @else {
                        {{ i18n.t('maint.cost.notShown') }}
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('maint.cost.restsOn')">
                      <app-status [tone]="elementTone(e.status)">{{ elementWord(e.status) }}</app-status>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          @if (c.labourLines.length) {
            <h3 class="cost-sub">{{ i18n.t('maint.cost.labourLines') }}</h3>
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('maint.hours.employee') }}</th>
                    <th>{{ i18n.t('maint.cost.month') }}</th>
                    <th class="num">{{ i18n.t('maint.hours.hours') }}</th>
                    <th>{{ i18n.t('maint.cost.rateKind') }}</th>
                    @if (c.labourMoneyShown) {
                      <th class="num">{{ i18n.t('maint.cost.rate') }}</th>
                      <th class="num">{{ i18n.t('maint.cost.amount') }}</th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (l of c.labourLines; track l.employeeId + l.year + '-' + l.month) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maint.hours.employee')">{{ l.employeeName }} <span class="ui-sub mono" dir="ltr">{{ l.employeeNumber }}</span></td>
                      <td class="mono" [attr.data-label]="i18n.t('maint.cost.month')"><span dir="ltr">{{ pad(l.month) }}/{{ l.year }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.hours.hours')"><span dir="ltr">{{ qty(l.hours) }}</span></td>
                      <td [attr.data-label]="i18n.t('maint.cost.rateKind')">
                        {{ i18n.t('maint.cost.rateKind.' + l.rateKind) }}
                        @if (l.withoutEmployerSocial) {
                          <span class="ui-sub">{{ i18n.t('maint.cost.withoutSocial') }}</span>
                        }
                      </td>
                      @if (c.labourMoneyShown) {
                        <td class="num mono" [attr.data-label]="i18n.t('maint.cost.rate')">{{ l.rate === null ? '—' : i18n.money(l.rate) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('maint.cost.amount')">{{ l.cost === null ? '—' : i18n.money(l.cost) }}</td>
                      }
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }

          @if (c.notes.length) {
            <ul class="notes">
              @for (n of c.notes; track n.code) {
                <li class="bo-hint">{{ note(n.code, n.value) }}</li>
              }
            </ul>
          }
        }
      </div>
    </section>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .cost {
        display: grid;
        gap: var(--sp-3);
      }
      .cost > .bo-hint {
        margin: 0;
      }
      .cost-sub {
        margin: 0;
        font-size: var(--fs-sm, 0.95rem);
      }
      .notes {
        display: grid;
        gap: var(--sp-1);
        margin: 0;
        padding-inline-start: var(--sp-5);
      }
    `,
  ],
})
export class MaintenanceJobCostComponent implements OnInit {
  private readonly api = inject(MaintenanceCostApi);
  readonly i18n = inject(I18nService);

  readonly job = input.required<MaintenanceJob>();

  readonly cost = signal<MaintenanceJobCost | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.cost.set(await this.api.jobCost(this.job().id));
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.loading.set(false);
    }
  }

  /** The four elements in the order the plan writes C: parts + labour + outside work + overhead. */
  elements(c: MaintenanceJobCost): { key: string; amount: number | null; status: string }[] {
    return [
      { key: 'parts', amount: c.parts, status: c.statuses.parts },
      { key: 'labour', amount: c.labour, status: c.statuses.labour },
      { key: 'outsideWork', amount: c.outsideWork, status: c.statuses.outsideWork },
      { key: 'overhead', amount: c.overhead, status: c.statuses.overhead },
    ];
  }

  /** What the cost is made of under the settings, in one sentence: MS-10, MS-12 and the accountant's confirmation. */
  basis(c: MaintenanceJobCost): string {
    const rate = c.labourRate === 'WorkshopRate' && c.workshopRatePerHour !== null
      ? this.i18n.t('maint.cost.basis.workshop', { rate: this.i18n.money(c.workshopRatePerHour) })
      : this.i18n.t('maint.cost.basis.payroll');
    return [
      this.i18n.t('maint.cost.basis.' + c.costIncludes),
      c.costIncludes === 'PartsAndOutsideWorkOnly' ? '' : rate,
      this.i18n.t(c.policyApproved ? 'maint.cost.basis.confirmed' : 'maint.cost.basis.unconfirmed'),
    ].filter((s) => !!s).join(' ');
  }

  note(code: string, value: number | null): string {
    return costNote(code, value, (k, p) => this.i18n.t(k, p), (n) => this.i18n.money(n), (n) => this.qty(n));
  }

  elementWord(status: string): string {
    return status === 'excluded' ? this.i18n.t('maint.cost.elementStatus.excluded') : this.i18n.t('mfg.cost.elementStatus.' + status);
  }

  sheetTone(status: string) {
    return toneOf('costSheet', status);
  }

  elementTone(status: string) {
    return toneOf('costElement', status);
  }

  qty(value: number): string {
    return formatQty(value, this.i18n.lang());
  }

  pad(month: number): string {
    return String(month).padStart(2, '0');
  }
}
