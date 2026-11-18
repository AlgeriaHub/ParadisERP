import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import { MonthInputComponent } from '../payroll/month-input.component';
import { parsePeriod } from '../payroll/payroll-run-view';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, ProductionLineSummary } from './manufacturing-api.service';
import { LabourAbsorptionPageSummary, ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';

/**
 * Manufacturing › Labour cost (costing, Phase A — R4 of docs/manufacturing/costing-plan.md): for one payroll
 * month, each employee booked on the lines the reader reaches — hours on orders and on other rows of the
 * daily records, the hours payroll paid, and, for a holder of payroll.manage, what payroll cost, what the
 * orders absorbed at the month's rate and what stayed in the wage charges. A month not approved in payroll
 * reads at the standard rate and says so.
 */
@Component({
  selector: 'app-mfg-labour-cost',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ListFooterComponent, MonthInputComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="lc-month">{{ i18n.t('mfg.cost.payrollMonth') }}</label>
          <app-month-input id="lc-month" [ngModel]="shownMonth()" (ngModelChange)="month.set($event)" />
        </div>
        <div class="ui-filter">
          <label for="lc-line">{{ i18n.t('mfg.boms.line') }}</label>
          <select id="lc-line" [ngModel]="lineId()" (ngModelChange)="lineId.set($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.orders.allLines') }}</option>
            @for (l of lines(); track l.id) {
              <option [ngValue]="l.id">{{ l.code }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="lc-basis">{{ i18n.t('mfg.cost.basis') }}</label>
          <select id="lc-basis" [ngModel]="basisFilter()" (ngModelChange)="basisFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (b of bases(); track b) {
              <option [ngValue]="b">{{ i18n.t('mfg.cost.basisWord.' + b) }}</option>
            }
          </select>
        </div>
        <div class="ui-toolbar__end">
          @if (basisFilter() !== 'all') {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      <p class="bo-hint">{{ i18n.t('mfg.cost.labourNotice') }}</p>
      @if (error() && data()?.all) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (data(); as d) {
        <div class="ui-metrics">
          @if (d.moneyShown) {
            <div class="ui-metric ui-metric--lead"><span class="ui-metric__label">{{ i18n.t('mfg.cost.absorbed') }}</span><strong class="ui-metric__value mono">{{ money(d.absorbedCost) }}</strong></div>
            <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.cost.employerCost') }}</span><strong class="ui-metric__value mono">{{ money(d.employerCost) }}</strong></div>
            <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.cost.unabsorbed') }}</span><strong class="ui-metric__value mono">{{ money(d.unabsorbedCost) }}</strong></div>
          }
          <div class="ui-metric" [class.ui-metric--lead]="!d.moneyShown"><span class="ui-metric__label">{{ i18n.t('mfg.cost.orderHours') }}</span><strong class="ui-metric__value mono">{{ qty(d.orderHours) }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.cost.otherHours') }}</span><strong class="ui-metric__value mono">{{ qty(d.otherHours) }}</strong></div>
        </div>
        @if (!d.moneyShown) {
          <p class="bo-hint">{{ i18n.t('mfg.cost.moneyHidden') }}</p>
        }
        @if (notApproved(d)) {
          <p class="bo-hint">{{ i18n.t('mfg.cost.notApprovedHint') }}</p>
        }
        @if (d.employerSocialMissing) {
          <p class="bo-hint">{{ i18n.t('mfg.cost.employerSocialHint') }}</p>
        }
        @if (d.labourAbsorption === 'PremiumToOvertime') {
          <!-- C-05: the premium is kept to hours marked overtime; what no order hour carried stays in the wage charges. -->
          <p class="bo-hint">
            {{ d.premiumInCharges === null || d.premiumInCharges === undefined
              ? i18n.t('mfg.cost.premiumHint') : i18n.t('mfg.cost.premiumHintAmount', { value: i18n.money(d.premiumInCharges) }) }}
          </p>
        }
        @if (d.lines?.length) {
          <section class="ui-surface" [attr.aria-label]="i18n.t('mfg.cost.byLineMonth')">
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <caption class="sr-only">{{ i18n.t('mfg.cost.byLineMonth') }}</caption>
                <thead>
                  <tr>
                    <th>{{ i18n.t('mfg.boms.line') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.ordersCount') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.workersCount') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.orderHours') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.lineLabour') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (l of d.lines; track l.lineId) {
                    <tr>
                      <td class="mono" [attr.data-label]="i18n.t('mfg.boms.line')">
                        {{ l.lineCode }}
                        @if (l.withheld) {
                          <span class="ui-sub">{{ i18n.t('mfg.cost.lineWithheld') }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.ordersCount')">{{ l.orders }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.workersCount')">{{ l.workers }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.orderHours')">{{ qty(l.orderHours) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.lineLabour')">{{ l.absorbedCost === null ? i18n.t('mfg.cost.withheld') : i18n.money(l.absorbedCost) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </section>
        }
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !data()?.all) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!data()) {
          <app-table-state kind="loading" />
        } @else if (!data()?.all) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.cost.noLabour')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.records.employee') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.orderHours') }}</th>
                  <th class="num">{{ i18n.t('mfg.cost.otherHours') }}</th>
                  <th>{{ i18n.t('mfg.cost.basis') }}</th>
                  @if (data()!.moneyShown) {
                    <th class="num">{{ i18n.t('mfg.cost.paidHours') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.employerCost') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.rate') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.absorbed') }}</th>
                    <th class="num">{{ i18n.t('mfg.cost.unabsorbed') }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (r of pager.rows(); track r.employeeId) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfg.records.employee')">{{ r.employeeName }} <span class="ui-sub mono" dir="ltr">{{ r.employeeNumber }}</span></td>
                    <td class="num" [attr.data-label]="i18n.t('mfg.cost.orderHours')">
                      <span class="mono">{{ qty(r.orderHours) }}</span>
                      @if (r.overtimeHours) {
                        <span class="ui-sub">{{ i18n.t('mfg.records.overtimeOf', { n: qty(r.overtimeHours) }) }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.otherHours')">{{ qty(r.otherHours) }}</td>
                    <td [attr.data-label]="i18n.t('mfg.cost.basis')">
                      {{ i18n.t('mfg.cost.basisWord.' + r.basis) }}
                      @if (r.reason) {
                        <span class="ui-sub">{{ i18n.t('mfg.cost.reason.' + r.reason) }}</span>
                      }
                    </td>
                    @if (data()!.moneyShown) {
                      <td class="num" [attr.data-label]="i18n.t('mfg.cost.paidHours')">
                        <span class="mono">{{ r.paidHours === null ? '—' : qty(r.paidHours) }}</span>
                        @if (r.paidOvertimeHours) {
                          <span class="ui-sub">{{ i18n.t('mfg.records.overtimeOf', { n: qty(r.paidOvertimeHours) }) }}</span>
                        }
                      </td>
                      <td class="num" [attr.data-label]="i18n.t('mfg.cost.employerCost')">
                        <span class="mono">{{ money(r.employerCost) }}</span>
                        @if (r.withoutEmployerSocial) {
                          <span class="ui-sub">{{ i18n.t('mfg.cost.withoutEmployerSocial') }}</span>
                        }
                        @if (r.overtimePremium) {
                          <span class="ui-sub">{{ i18n.t('mfg.cost.overtimePremium', { value: i18n.money(r.overtimePremium) }) }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.rate')">{{ money(r.rate) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('mfg.cost.absorbed')">{{ money(r.absorbedCost) }}</td>
                      <td class="num" [attr.data-label]="i18n.t('mfg.cost.unabsorbed')">
                        <span class="mono">{{ money(r.unabsorbedCost) }}</span>
                        @if (r.premiumInCharges) {
                          <span class="ui-sub">{{ i18n.t('mfg.cost.premiumInCharges', { value: i18n.money(r.premiumInCharges) }) }}</span>
                        }
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MfgLabourCostComponent implements OnInit {
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly api = inject(ManufacturingApiService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly qty = qty;
  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly lineId = signal<string | null>(null);
  /**
   * `YYYY-MM` as the reader chose it. Empty until they choose: the server answers the company's month today, and the
   * box shows the month it answered (`shownMonth`) without asking again.
   */
  readonly month = signal('');

  /** The rate each employee's hours are read at, filtered on the server. The month and the line are the report's own. */
  readonly basisFilter = signal<string>('all');
  /**
   * The employees a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"). The
   * summary is the report without its rows: its month, tiles, per-line table and notes, over every employee.
   */
  readonly pager = createServerPager(
    (page) => this.pages.labourAbsorption({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.labourCost',
      filters: () => {
        const period = parsePeriod(this.month());
        return { year: period?.year ?? null, month: period?.month ?? null, lineId: this.lineId(), basis: this.basisFilter() };
      },
    },
  );
  /** The report without its rows; null until the first answer. */
  readonly data = computed(() => this.pager.summary());
  /** The month on screen: the one chosen, else the one the server answered. */
  readonly shownMonth = computed(() => {
    const d = this.data();
    return this.month() || (d ? `${d.year}-${String(d.month).padStart(2, '0')}` : '');
  });
  /** The bases the report holds, in the order they first appear. */
  readonly bases = computed(() => this.data()?.bases ?? []);

  /** The report could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.basisFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.cost.labourLead'));
    this.api.lines(true).then((l) => this.lines.set(l), () => undefined);
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  /** Some of the month is not approved in payroll: its hours read at the standard rate and have no employer cost yet. */
  notApproved(report: LabourAbsorptionPageSummary): boolean {
    return report.notApproved;
  }

  money(value: number | null): string {
    return value === null ? '—' : this.i18n.money(value);
  }
}
