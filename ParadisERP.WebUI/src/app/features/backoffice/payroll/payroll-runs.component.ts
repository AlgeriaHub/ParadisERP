import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { StatusComponent } from '../../../ui/status.component';
import { StatusTone, toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { localizedName } from '../backoffice-format';
import { PAGE_CHROME } from '../shell/page-chrome';
import { MonthInputComponent } from './month-input.component';
import { PayrollPagesApi } from './payroll-pages.api';
import { PayrollRunComponent } from './payroll-run.component';
import { moneyErrorMessage } from './wage-money-api.service';
import {
  PayrollRun,
  PayrollRunStatus,
  PayrollRunsApi,
  parsePeriod,
  periodInput,
  periodLabel,
  periodOf,
  stateKey,
} from './payroll-run-view';

/** The lifecycle as chips, "all" first. Spelled as the server spells the values. */
const STATUSES: readonly PayrollRunStatus[] = ['Draft', 'Locked', 'Approved', 'Paid', 'Reversed'];

/**
 * **Whose wages have been run this month, where they stand, and what they come to.**
 *
 * <p>A run is one document per branch per month. Opening a month is therefore the ordinary act
 * this screen exists for, and it is the only thing here that writes: everything else is a list
 * and a way into one run. A month a branch has already opened is refused by the server with its
 * reason, and the reason is shown rather than translated into a disabled button — a run that was
 * reversed leaves the month open again, and only the server knows that.</p>
 *
 * <p>The list carries the figures the owner asks for across the counter — head count, gross, net
 * — and nothing else; the six tiles, the warnings and the lines belong to the run itself. A run
 * computed while the statutory figures were still placeholders says <b>provisional</b> here too,
 * because that is the first place somebody sees it.</p>
 */
@Component({
  selector: 'app-payroll-runs',
  standalone: true,
  imports: [
    FormsModule,
    PayrollRunComponent,
    MonthInputComponent,
    ...PAGE_CHROME,
    StatusComponent,
    TableStateComponent,
    ListFooterComponent,
  ],
  template: `
    @if (openRunId(); as id) {
      <app-payroll-run [runId]="id" (back)="closeRun()" />
    } @else {
      <ng-template appPageToolbar>
        <div class="ui-toolbar" role="search">
          @if (lock.locked()) {
            <p class="bo-scope-note" id="runf-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
          } @else {
            <div class="ui-filter">
              <label for="run-filter-branch">{{ i18n.t('hr.runs.branch') }}</label>
              <select id="run-filter-branch" [ngModel]="branchId()" (ngModelChange)="pickBranch($event)">
                <option value="">{{ i18n.t('hr.runs.allBranches') }}</option>
                @for (branch of branches(); track branch.id) {
                  <option [value]="branch.id">{{ branchName(branch) }}</option>
                }
              </select>
            </div>
          }
          <div class="ui-filter ui-filter--narrow">
            <label for="run-filter-year">{{ i18n.t('hr.runs.year') }}</label>
            <select id="run-filter-year" class="mono" [ngModel]="year()" (ngModelChange)="pickYear($event)">
              @for (y of years; track y) {
                <option [value]="y">{{ y }}</option>
              }
            </select>
          </div>
          <div class="ui-filter">
            <span class="ui-filter__label" id="run-state-label">{{ i18n.t('common.state') }}</span>
            <div class="bo-chips" role="group" aria-labelledby="run-state-label">
              <button type="button" [class.is-on]="!status()" [attr.aria-pressed]="!status()" (click)="pickStatus(null)">
                {{ i18n.t('hr.runs.status.all') }}
              </button>
              @for (choice of statuses; track choice) {
                <button type="button" [class.is-on]="status() === choice" [attr.aria-pressed]="status() === choice" (click)="pickStatus(choice)">
                  {{ i18n.t(stateWord(choice)) }}
                </button>
              }
            </div>
          </div>
          <div class="ui-toolbar__end">
            <button type="button" class="btn-ghost" [disabled]="busy()" (click)="load()">{{ i18n.t('subscription.refresh') }}</button>
          </div>
        </div>
      </ng-template>

      <div class="bo-stack">
        @if (error() || listError()) {
          <p class="notice danger" role="alert">{{ error() || listError() }}</p>
        }
        @if (done()) {
          <p class="notice ok" role="status">{{ done() }}</p>
        }

        <!-- ---------- open a month: one row, the act this screen exists for ---------- -->
        <section class="ui-surface open-month" aria-labelledby="run-new-title">
          <div class="open-month__text">
            <h2 class="surface-title" id="run-new-title">{{ i18n.t('hr.runs.new') }}</h2>
            <p class="ui-sub">{{ i18n.t('hr.runs.newHint') }}</p>
          </div>
          <div class="ui-toolbar open-month__form">
            @if (lock.locked()) {
              <div class="ui-filter">
                <span class="ui-filter__label">{{ i18n.t('branchLock.branch') }}</span>
                <p class="bo-fixed" id="run-fixed">{{ lock.ownBranchName(branches()) }}</p>
              </div>
            } @else {
              <div class="ui-filter">
                <label for="run-branch">{{ i18n.t('hr.runs.branch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <select id="run-branch" [ngModel]="newBranchId()" (ngModelChange)="newBranchId.set($event)">
                  <option value="">—</option>
                  @for (branch of branches(); track branch.id) {
                    <option [value]="branch.id">{{ branchName(branch) }}</option>
                  }
                </select>
              </div>
            }
            <div class="ui-filter">
              <label for="run-period">{{ i18n.t('hr.runs.period') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <app-month-input id="run-period" [ngModel]="newPeriod()" (ngModelChange)="newPeriod.set($event)" />
            </div>
            <button type="button" class="btn-primary" [disabled]="!canCreate() || busy()" (click)="create()">
              {{ i18n.t('hr.runs.create') }}
            </button>
          </div>
        </section>

        <!-- ---------- what has been run ---------- -->
        <section class="ui-surface" [attr.aria-busy]="!pager.loaded()">
          @if (!pager.loaded()) {
            <app-table-state kind="loading" />
          } @else if (!pager.total()) {
            <app-table-state kind="empty" [message]="i18n.t('hr.runs.none') + ' ' + i18n.t('hr.runs.noneHint')" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards" [attr.aria-label]="i18n.t('hr.runs.title')">
                <thead>
                  <tr>
                    <th>{{ i18n.t('hr.runs.period') }}</th>
                    <th>{{ i18n.t('hr.runs.branch') }}</th>
                    <th>{{ i18n.t('hr.runs.number') }}</th>
                    <th>{{ i18n.t('common.state') }}</th>
                    <th class="num">{{ i18n.t('hr.runs.headCount') }}</th>
                    <th class="num">{{ i18n.t('hr.run.tile.gross') }}</th>
                    <th class="num">{{ i18n.t('hr.run.tile.net') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (run of pager.rows(); track run.id) {
                    <tr>
                      <td [attr.data-label]="i18n.t('hr.runs.period')">
                        <button type="button" class="ui-link" (click)="openRunId.set(run.id)">{{ period(run) }}</button>
                      </td>
                      <td [attr.data-label]="i18n.t('hr.runs.branch')">{{ run.branchName }}</td>
                      <td class="mono" [attr.data-label]="i18n.t('hr.runs.number')"><span dir="ltr">{{ run.number }}</span></td>
                      <td [attr.data-label]="i18n.t('common.state')">
                        <span class="bo-states">
                          <app-status [tone]="tone(run.status)">{{ i18n.t(stateWord(run.status)) }}</app-status>
                          @if (run.isProvisional) {
                            <app-status tone="warning">{{ i18n.t('hr.run.provisionalShort') }}</app-status>
                          }
                        </span>
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('hr.runs.headCount')">{{ run.headCount }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('hr.run.tile.gross')">{{ i18n.money(run.gross) }}</td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.run.tile.net')">{{ i18n.money(run.net) }}</td>
                    </tr>
                  }
                </tbody>
                <tfoot>
                  <tr>
                    <td colspan="4">{{ i18n.t('common.total') }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.runs.headCount')">{{ totalHeadCount() }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.run.tile.gross')">{{ i18n.money(totalGross()) }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.run.tile.net')">{{ i18n.money(totalNet()) }}</td>
                  </tr>
                </tfoot>
              </table>
            </div>
          }
        </section>
        <!-- Paged by the server; the total row is the server's sum over every run the filters keep (owner, 2026-11-17). -->
        <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
      </div>
    }
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .open-month {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        justify-content: space-between;
        gap: var(--sp-3) var(--sp-5);
        padding: var(--sp-4) var(--sp-5);
      }
      .open-month__text {
        flex: 1 1 16rem;
        min-inline-size: 0;
      }
      .open-month__text .surface-title {
        margin: 0;
        font-size: var(--fs-section);
      }
      .open-month__form {
        flex: 0 1 auto;
        inline-size: auto;
      }
      app-month-input {
        min-inline-size: 15rem;
      }
      .ui-filter--narrow {
        min-inline-size: 6.5rem;
      }
      .bo-fixed {
        margin: 0;
        min-block-size: var(--control-h-sm);
        display: flex;
        align-items: center;
        font-weight: 600;
      }
    `,
  ],
})
export class PayrollRunsComponent implements OnInit {
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly admin = inject(AdminService);
  private readonly api = inject(PayrollRunsApi);
  private readonly pages = inject(PayrollPagesApi);
  private readonly confirm = inject(ConfirmService);

  readonly statuses = STATUSES;
  /** This year and the two before it: payroll is read back, but not indefinitely. */
  readonly years = [0, 1, 2].map((back) => new Date().getFullYear() - back);

  readonly branches = signal<AdminBranch[]>([]);
  /** The list asks the server once the branch a branch-held user is confined to is known. */
  private readonly ready = signal(false);
  readonly branchId = signal('');
  readonly year = signal(new Date().getFullYear());
  readonly status = signal<PayrollRunStatus | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly openRunId = signal<string | null>(null);

  readonly newBranchId = signal('');
  readonly newPeriod = signal(periodInput(periodOf(new Date())));

  readonly canCreate = computed(() => !!this.newBranchId() && !!parsePeriod(this.newPeriod()));

  /**
   * The runs a page at a time from the server (owner, 2026-11-17: "paginate in backend"), ten to begin with. The
   * branch, the year and the state run there; the total row is the server's sum over every run they keep.
   */
  readonly pager = createServerPager(
    (page) => this.pages.runsPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'payroll.runs',
      filters: () => ({ branchId: this.branchId() || null, year: this.year(), status: this.status() }),
      enabled: () => this.ready(),
    },
  );

  readonly totalHeadCount = computed(() => this.pager.summary()?.headCount ?? 0);
  readonly totalGross = computed(() => this.pager.summary()?.gross ?? 0);
  readonly totalNet = computed(() => this.pager.summary()?.net ?? 0);

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)) : null;
  });

  async ngOnInit(): Promise<void> {
    this.branches.set(await this.admin.listBranches().catch(() => []));
    this.branchId.set(this.lock.lockedBranchId() ?? '');
    this.newBranchId.set(this.lock.defaultBranch(this.branches()) ?? '');
    // The list loads itself from here on, and again whenever a filter changes.
    this.ready.set(true);
  }

  /** Asks again for the page on screen: the refresh button, a new run, a run closed. */
  async load(): Promise<void> {
    this.error.set(null);
    await this.pager.reload();
  }

  async create(): Promise<void> {
    const period = parsePeriod(this.newPeriod());
    if (!this.newBranchId() || !period) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('hr.runs.create'))))) return;
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      const run = await this.api.create(this.newBranchId(), period.year, period.month);
      this.done.set(this.i18n.t('hr.runs.created', { number: run.number }));
      await this.load();
      this.openRunId.set(run.id);
    } catch (e) {
      // "A run already exists for this branch and period" is the server's sentence and the
      // reversal rule lives there, not here; it is shown, never pre-empted by a grey button.
      this.error.set(moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)));
    } finally {
      this.busy.set(false);
    }
  }

  async closeRun(): Promise<void> {
    this.openRunId.set(null);
    await this.load();
  }

  // A filter change goes back to page one and asks the server again (the pager's own effect).
  pickBranch(value: string): void {
    this.branchId.set(value);
  }

  pickYear(value: string | number): void {
    this.year.set(Number(value));
  }

  pickStatus(value: PayrollRunStatus | null): void {
    this.status.set(value);
  }

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  period(run: PayrollRun): string {
    return periodLabel({ year: run.periodYear, month: run.periodMonth }, this.i18n.lang());
  }

  stateWord(status: string): string {
    return stateKey('hr.run.status', status);
  }

  tone(status: PayrollRunStatus): StatusTone {
    return toneOf('payrollRun', status);
  }
}
