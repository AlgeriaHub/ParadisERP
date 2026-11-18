import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemCode } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { localizedName } from '../../../core/multilingual';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PracticeEntry, PracticeLine } from '../manufacturing/costing-settings-api.service';
import { PracticeLedgerTabsComponent } from '../manufacturing/practice-ledger-tabs.component';
import { MonthInputComponent } from '../payroll/month-input.component';
import { parsePeriod } from '../payroll/payroll-run-view';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { MaintenanceLedger, MaintenanceLedgerApi } from './maintenance-ledger-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { maintenanceError } from './maintenance-words';

/** The refusals this page says in the reader's language (the month poster's, shared with manufacturing). */
const REFUSALS: Readonly<Record<string, string>> = {
  payroll_details_withheld: 'maintLedger.error.withheld',
  posting_off: 'maintLedger.error.postingOff',
  month_not_ended: 'maintLedger.error.monthNotEnded',
  checks_not_passed: 'maintLedger.error.checks',
  already_posted: 'maintLedger.error.alreadyPosted',
  previous_month_not_posted: 'maintLedger.error.previous',
  before_posting_start: 'maintLedger.error.beforeStart',
  later_month_posted: 'maintLedger.error.later',
  not_posted: 'maintLedger.error.notPosted',
  post_needs_every_branch: 'maintLedger.error.everyBranch',
  books_closed: 'maintLedger.error.booksClosed',
};

/**
 * Maintenance › Practice ledger (Phase M6, docs/maintenance/plan.md §5 and §7): one month of the entries the accountant's
 * choices would make for maintenance — parts issued and returned against the stock they left; labour and overhead moved into
 * jobs (MS-33); open customer jobs' work in progress (MS-36) — with each account's total and the checks of the events against
 * the entries. Computed by the server on every read and posted nowhere until the accountant posts the closed month here
 * (MS-39, `accounting.manage`), or reverses the last one posted. The same patterns as Manufacturing › Practice ledger, whose
 * tab it is beside; documents posted at issue (MS-32), open customer jobs and customer jobs' invoices are listed below.
 * The entries and the lists below are paged by the server (owner, 2026-11-17: "paginate in backend, not frontend"; ten rows
 * to begin with). The entries take no filter: the tiles, the account totals and the checks — the entries page's summary —
 * add every one of them up. The lists below are filtered by the server, each in its own card: open jobs by branch, invoices
 * by whether they name a job, provisions by what they still hold; each is read only when the month has rows of it. Checks
 * and accounts stay whole.
 */
@Component({
  selector: 'app-maintenance-practice-ledger',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ListFooterComponent, MonthInputComponent, StatusComponent, TableStateComponent, PracticeLedgerTabsComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="ml-month">{{ i18n.t('mfgLedger.month') }}</label>
          <app-month-input id="ml-month" [ngModel]="month()" (ngModelChange)="asked.set($event ?? '')" />
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="busy() || entryPager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      <app-practice-ledger-tabs current="maintenance" />

      @if (actionError() && data()) {
        <p class="notice danger" role="alert">{{ actionError() }}</p>
      }

      @if (data(); as d) {
        <p class="notice">
          {{ i18n.t('maintLedger.choices', {
            labour: i18n.t('maint.labourInBooks.' + d.labourInBooks),
            open: i18n.t('maint.openJobsAtPeriodEnd.' + d.openJobsAtPeriodEnd),
            parts: i18n.t('maint.partsPosting.' + d.partsPosting) }) }}
          @if (d.monthToDate) {
            · {{ i18n.t('mfgLedger.note.month_to_date') }}
          }
        </p>
        <div class="ui-metrics">
          <div class="ui-metric ui-metric--lead">
            <span class="ui-metric__label">{{ i18n.t('mfgLedger.totalDebit') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(d.totalDebit) }}</strong>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('mfgLedger.entries') }}</span>
            <strong class="ui-metric__value mono">{{ entryPager.total() }}</strong>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('mfgLedger.checksPassed') }}</span>
            <strong class="ui-metric__value mono">{{ passed() }} / {{ d.checks.length }}</strong>
          </div>
        </div>

        <!-- MS-39: the accountant posts the closed month to the ledger from here, or takes the last one back. -->
        <section class="ui-surface ml-posting" [attr.aria-label]="i18n.t('mfgLedger.posting')">
          <h2 class="bo-panel__title">{{ i18n.t('mfgLedger.posting') }}</h2>
          @if (!d.postingFromMonth) {
            <p class="bo-hint">{{ i18n.t('maintLedger.postingOff') }}</p>
          } @else {
            <p class="bo-hint">{{ i18n.t('mfgLedger.postingFrom', { month: monthName(d.postingFromMonth) }) }}</p>
          }
          @for (p of d.postings; track p.generation) {
            <p>
              {{ i18n.t('mfgLedger.posted', { who: p.postedBy ?? '—', date: i18n.dateTime(p.postedAtUtc), entries: p.entries, total: i18n.money(p.totalDebit) }) }}
              @if (p.reversedAtUtc) {
                · {{ i18n.t('mfgLedger.reversed', { who: p.reversedBy ?? '—', date: i18n.dateTime(p.reversedAtUtc) }) }}
              }
            </p>
          }
          @if (canPost()) {
            <div class="ml-actions">
              @if (live()) {
                <button type="button" class="btn-secondary is-danger" [disabled]="busy()" (click)="reverse()">{{ i18n.t('mfgLedger.reverse') }}</button>
              } @else if (d.postingFromMonth) {
                <button type="button" class="btn-primary" [disabled]="busy() || d.monthToDate" (click)="post()">{{ i18n.t('mfgLedger.post') }}</button>
              }
            </div>
          }
        </section>

        @if (d.unmappedSlots.length) {
          <p class="notice warn" role="status">{{ i18n.t('mfgLedger.unmapped', { slots: unmappedNames() }) }}</p>
        }

        <section class="ui-surface" [attr.aria-label]="i18n.t('mfgLedger.checks')">
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <caption class="sr-only">{{ i18n.t('mfgLedger.checks') }}</caption>
              <thead>
                <tr>
                  <th>{{ i18n.t('mfgLedger.check') }}</th>
                  <th>{{ i18n.t('mfgLedger.result') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.expected') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.actual') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (c of d.checks; track c.code) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfgLedger.check')">{{ checkName(c.code) }}</td>
                    <td [attr.data-label]="i18n.t('mfgLedger.result')">
                      <app-status [tone]="toneOf('costCheck', c.passed ? 'pass' : 'fail')">
                        {{ i18n.t(c.passed ? 'mfgLedger.pass' : 'mfgLedger.fail') }}
                      </app-status>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.expected')">{{ figure(c.code, c.expected) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.actual')">{{ figure(c.code, c.actual) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }

      <section class="ui-surface" [attr.aria-busy]="entryPager.loading()" [attr.aria-label]="i18n.t('mfgLedger.entriesTitle')">
        @if (!data() && !listError()) {
          <app-table-state kind="loading" />
        } @else if (!data()) {
          <app-table-state kind="error" [message]="listError()" (retry)="reload()" />
        } @else if (!entryPager.total()) {
          <app-table-state kind="empty" [message]="i18n.t('maintLedger.empty')" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <caption class="sr-only">{{ i18n.t('mfgLedger.entriesTitle') }}</caption>
              <thead>
                <tr>
                  <th>{{ i18n.t('mfgLedger.day') }}</th>
                  <th>{{ i18n.t('mfgLedger.entry') }}</th>
                  <th>{{ i18n.t('mfgLedger.account') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.debit') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.credit') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (e of entryPager.rows(); track $index) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('mfgLedger.day')">{{ i18n.date(e.day) }}</td>
                    <td [attr.data-label]="i18n.t('mfgLedger.entry')">
                      {{ i18n.t('maintLedger.kind.' + e.kind) }}
                      <span class="ui-sub"><span class="mono" dir="ltr">{{ e.orderNumber }}</span>
                        @if (e.document) {
                          · <span class="mono" dir="ltr">{{ e.document }}</span>
                        }
                        · {{ branchName(e) }}</span>
                    </td>
                    <td [attr.data-label]="i18n.t('mfgLedger.account')">
                      @for (l of e.lines; track $index) {
                        <div class="ml-line" [class.ml-line--credit]="l.credit > 0">{{ accountLabel(l) }}</div>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.debit')">
                      @for (l of e.lines; track $index) {
                        <div>{{ l.debit ? i18n.money(l.debit) : '' }}&nbsp;</div>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.credit')">
                      @for (l of e.lines; track $index) {
                        <div>{{ l.credit ? i18n.money(l.credit) : '' }}&nbsp;</div>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
      <app-list-footer [pager]="entryPager" [loading]="!data()" />

      @if (data()?.accounts?.length) {
        <section class="ui-surface" [attr.aria-label]="i18n.t('mfgLedger.accountsTitle')">
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <caption class="sr-only">{{ i18n.t('mfgLedger.accountsTitle') }}</caption>
              <thead>
                <tr>
                  <th>{{ i18n.t('mfgLedger.account') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.debit') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.credit') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.balance') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (a of data()!.accounts; track a.slot) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfgLedger.account')">{{ accountLabel(a) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.debit')">{{ i18n.money(a.debit) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.credit')">{{ i18n.money(a.credit) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.balance')">{{ i18n.money(a.debit - a.credit) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }

      @if (counts().postedAtIssue) {
        <section class="ui-surface" [attr.aria-label]="i18n.t('maintLedger.atIssueTitle')">
          <div class="ui-surface__head">
            <h2>{{ i18n.t('maintLedger.atIssueTitle') }}</h2>
          </div>
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <caption class="sr-only">{{ i18n.t('maintLedger.atIssueTitle') }}</caption>
              <thead>
                <tr>
                  <th>{{ i18n.t('mfgLedger.day') }}</th>
                  <th>{{ i18n.t('maintLedger.document') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.amount') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (p of issuePager.rows(); track p.document) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('mfgLedger.day')">{{ i18n.date(p.day) }}</td>
                    <td [attr.data-label]="i18n.t('maintLedger.document')">
                      <span class="mono" dir="ltr">{{ p.document }}</span> · <span class="mono" dir="ltr">{{ p.jobNumber }}</span>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.amount')">{{ i18n.money(p.value) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
        <app-list-footer [pager]="issuePager" [loading]="!issuePager.loaded()" />
      }

      @if (counts().openCustomerJobs) {
        <section class="ui-surface" [attr.aria-label]="i18n.t('maintLedger.openTitle')">
          <div class="ui-surface__head">
            <h2>{{ i18n.t('maintLedger.openTitle') }}</h2>
          </div>
          @if (openJobBranches().length > 1) {
            <div class="ui-surface__body">
              <div class="ui-toolbar" role="search">
                <div class="ui-filter">
                  <label for="ml-f-open-branch">{{ i18n.t('filters.branch') }}</label>
                  <select id="ml-f-open-branch" [ngModel]="openJobBranch()" (ngModelChange)="openJobBranch.set($event)">
                    <option [ngValue]="null">{{ i18n.t('filters.all') }}</option>
                    @for (b of openJobBranches(); track b) {
                      <option [ngValue]="b">{{ b }}</option>
                    }
                  </select>
                </div>
              </div>
            </div>
          }
          @if (!openJobPager.loaded()) {
            <app-table-state kind="loading" />
          } @else if (!openJobPager.total()) {
            <app-table-state kind="noResults" (clear)="clearFilters()" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <caption class="sr-only">{{ i18n.t('maintLedger.openTitle') }}</caption>
                <thead>
                  <tr>
                    <th>{{ i18n.t('maintLedger.job') }}</th>
                    <th class="num">{{ i18n.t('maintLedger.costToDate') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (o of openJobPager.rows(); track o.jobId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maintLedger.job')"><span class="mono" dir="ltr">{{ o.jobNumber }}</span> · {{ o.branchCode }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maintLedger.costToDate')">
                        {{ o.costToDate === null ? i18n.t('maint.cost.notShown') : i18n.money(o.costToDate) }}
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
        <app-list-footer [pager]="openJobPager" [loading]="!openJobPager.loaded()" />
      }

      @if (counts().invoices) {
        <section class="ui-surface" [attr.aria-label]="i18n.t('maintLedger.invoicesTitle')">
          <div class="ui-surface__head">
            <h2>{{ i18n.t('maintLedger.invoicesTitle') }}</h2>
          </div>
          <div class="ui-surface__body">
            <div class="ui-toolbar" role="search">
              <div class="ui-filter">
                <label for="ml-f-matched">{{ i18n.t('maintLedger.job') }}</label>
                <select id="ml-f-matched" [ngModel]="invoiceMatch()" (ngModelChange)="invoiceMatch.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="matched">{{ i18n.t('maint.filter.matched') }}</option>
                  <option ngValue="unmatched">{{ i18n.t('maintLedger.invoiceUnmatched') }}</option>
                </select>
              </div>
            </div>
          </div>
          @if (!invoicePager.loaded()) {
            <app-table-state kind="loading" />
          } @else if (!invoicePager.total()) {
            <app-table-state kind="noResults" (clear)="clearFilters()" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <caption class="sr-only">{{ i18n.t('maintLedger.invoicesTitle') }}</caption>
                <thead>
                  <tr>
                    <th>{{ i18n.t('mfgLedger.day') }}</th>
                    <th>{{ i18n.t('maintLedger.invoice') }}</th>
                    <th>{{ i18n.t('maintLedger.job') }}</th>
                    <th class="num">{{ i18n.t('maintLedger.net') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (v of invoicePager.rows(); track v.number) {
                    <tr>
                      <td class="mono" [attr.data-label]="i18n.t('mfgLedger.day')">{{ i18n.date(v.day) }}</td>
                      <td class="mono" dir="ltr" [attr.data-label]="i18n.t('maintLedger.invoice')">{{ v.number }}</td>
                      <td [attr.data-label]="i18n.t('maintLedger.job')">
                        @if (v.jobMatched) {
                          <span class="mono" dir="ltr">{{ v.jobNumber }}</span>
                        } @else {
                          <app-status [tone]="toneOf('costCheck', 'fail')">{{ i18n.t('maintLedger.invoiceUnmatched') }}</app-status>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maintLedger.net')">{{ i18n.money(v.netAmount) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
        <app-list-footer [pager]="invoicePager" [loading]="!invoicePager.loaded()" />
      }

      <!-- Phase M7c (MS-37 "provision"): each guarantee provision the month moves, booked, used and released in words. -->
      @if (counts().provisions) {
        <section class="ui-surface" [attr.aria-label]="i18n.t('maintLedger.provisionsTitle')">
          <div class="ui-surface__head">
            <h2>{{ i18n.t('maintLedger.provisionsTitle') }}</h2>
          </div>
          <div class="ui-surface__body">
            <div class="ui-toolbar" role="search">
              <div class="ui-filter">
                <label for="ml-f-held">{{ i18n.t('maintLedger.provision.held') }}</label>
                <select id="ml-f-held" [ngModel]="provisionHeld()" (ngModelChange)="provisionHeld.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
                  <option ngValue="no">{{ i18n.t('filters.no') }}</option>
                </select>
              </div>
            </div>
          </div>
          @if (!provisionPager.loaded()) {
            <app-table-state kind="loading" />
          } @else if (!provisionPager.total()) {
            <app-table-state kind="noResults" (clear)="clearFilters()" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <caption class="sr-only">{{ i18n.t('maintLedger.provisionsTitle') }}</caption>
                <thead>
                  <tr>
                    <th>{{ i18n.t('maintLedger.job') }}</th>
                    <th>{{ i18n.t('maintLedger.guaranteeUntil') }}</th>
                    <th class="num">{{ i18n.t('maintLedger.provision.amount') }}</th>
                    <th class="num">{{ i18n.t('maintLedger.provision.booked') }}</th>
                    <th class="num">{{ i18n.t('maintLedger.provision.used') }}</th>
                    <th class="num">{{ i18n.t('maintLedger.provision.released') }}</th>
                    <th class="num">{{ i18n.t('maintLedger.provision.held') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (p of provisionPager.rows(); track p.jobId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maintLedger.job')">
                        <span class="mono" dir="ltr">{{ p.jobNumber }}</span>
                        @if (p.invoiceNumber) {
                          · <span class="mono" dir="ltr">{{ p.invoiceNumber }}</span>
                        }
                      </td>
                      <td class="mono" [attr.data-label]="i18n.t('maintLedger.guaranteeUntil')">{{ i18n.date(p.guaranteeUntil) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maintLedger.provision.amount')">{{ i18n.money(p.amount) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maintLedger.provision.booked')">{{ i18n.money(p.booked) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maintLedger.provision.used')">{{ i18n.money(p.used) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maintLedger.provision.released')">{{ i18n.money(p.released) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maintLedger.provision.held')">{{ i18n.money(p.heldAtEnd) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
        <app-list-footer [pager]="provisionPager" [loading]="!provisionPager.loaded()" />
      }

      @if (data(); as d) {
        <ul class="bo-hint ml-notes">
          @for (n of d.notes; track n) {
            @if (n !== 'month_to_date') {
              <li>{{ i18n.t('maintLedger.note.' + n) }}</li>
            }
          }
        </ul>
      }
    </div>
  `,
  styles: [
    `
      .ml-line--credit {
        padding-inline-start: var(--sp-4);
      }
      .ml-posting {
        padding: var(--sp-4);
        display: grid;
        gap: var(--sp-2);
      }
      .ml-posting p {
        margin: 0;
      }
      .ml-actions {
        display: flex;
        justify-content: flex-end;
      }
      .ml-notes {
        margin: 0;
        padding-inline-start: var(--sp-4);
      }
    `,
  ],
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenancePracticeLedgerComponent implements OnInit {
  private readonly api = inject(MaintenanceLedgerApi);
  private readonly pages = inject(MaintenancePagesApi);
  private readonly chrome = inject(PageChrome, { optional: true });
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly toneOf = toneOf;
  /** Posting and reversing are the accountant's: accounting.manage, checked again by the server. */
  readonly canPost = computed(() => mayDo(this.auth.currentUser(), 'accounting.manage'));
  /** The month's posting that stands (not reversed), if any. */
  readonly live = computed(() => (this.data()?.postings ?? []).some((p) => !p.reversedAtUtc));

  /** The month asked (`YYYY-MM`); empty until the reader picks one: the server reads the company's month today. */
  readonly asked = signal('');
  readonly busy = signal(false);
  /** A post or a reversal refused, in words. */
  readonly actionError = signal<string | null>(null);

  /**
   * The month's entries a page at a time from the server (owner, 2026-11-17). The summary is the month without its lists —
   * tiles, account totals, checks, postings, notes, all over every entry — and how many rows each list below holds.
   */
  readonly entryPager = createServerPager(
    (page) => this.pages.ledgerEntries({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'maintenance.ledger.entries',
      filters: () => {
        const period = parsePeriod(this.asked());
        return { year: period?.year ?? null, month: period?.month ?? null };
      },
      debounceMs: 0,
    },
  );
  private readonly summary = computed(() => this.entryPager.summary());
  /** The month as the server read it; none while it cannot be read (an error says why). */
  readonly data = computed<MaintenanceLedger | null>(() => (this.entryPager.error() ? null : this.summary()?.ledger ?? null));
  /** The month on screen: the one asked, else the company's month the server answered with. */
  readonly month = computed(() => {
    const d = this.data();
    return this.asked() || (d ? `${d.year}-${String(d.month).padStart(2, '0')}` : '');
  });
  /** How many rows each list below the entries holds this month: a card shows, and its rows are read, only when some. */
  readonly counts = computed(() => {
    const s = this.data() ? this.summary() : null;
    return {
      postedAtIssue: s?.postedAtIssue ?? 0,
      openCustomerJobs: s?.openCustomerJobs ?? 0,
      invoices: s?.invoices ?? 0,
      provisions: s?.provisions ?? 0,
    };
  });
  /** The month the lists below read: the one the entries were read for. */
  private readonly period = computed(() => {
    const d = this.data();
    return { year: d?.year ?? null, month: d?.month ?? null };
  });

  readonly passed = computed(() => this.data()?.checks.filter((c) => c.passed).length ?? 0);
  readonly unmappedNames = computed(() => (this.data()?.unmappedSlots ?? []).map((s) => this.slotName(s)).join(' · '));

  // The lists' filters (owner, 2026-11-17). Each is sent to the server, which filters every row of the month.
  readonly openJobBranch = signal<string | null>(null);
  readonly invoiceMatch = signal<'all' | 'matched' | 'unmatched'>('all');
  readonly provisionHeld = signal<'all' | 'yes' | 'no'>('all');

  readonly filtered = computed(
    () => !!this.openJobBranch() || this.invoiceMatch() !== 'all' || this.provisionHeld() !== 'all',
  );

  /** Ten rows to begin with, 20, 50 or 100 at the user's choice; another month or filter starts at page one. */
  readonly issuePager = createServerPager(
    (page) => this.pages.ledgerPostedAtIssue({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'maintenance.ledger.postedAtIssue', filters: () => this.period(), enabled: () => this.counts().postedAtIssue > 0, debounceMs: 0 },
  );
  readonly openJobPager = createServerPager(
    (page) => this.pages.ledgerOpenJobs({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'maintenance.ledger.openJobs',
      filters: () => ({ ...this.period(), branchCode: this.openJobBranch() }),
      enabled: () => this.counts().openCustomerJobs > 0,
    },
  );
  readonly invoicePager = createServerPager(
    (page) => this.pages.ledgerInvoices({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'maintenance.ledger.invoices',
      filters: () => ({ ...this.period(), match: this.invoiceMatch() }),
      enabled: () => this.counts().invoices > 0,
    },
  );
  readonly provisionPager = createServerPager(
    (page) => this.pages.ledgerProvisions({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'maintenance.ledger.provisions',
      filters: () => ({ ...this.period(), held: this.provisionHeld() }),
      enabled: () => this.counts().provisions > 0,
    },
  );

  /** The branches holding open customer jobs, by code as the table shows them (the server's): several only for the Admin. */
  readonly openJobBranches = computed(() => this.openJobPager.summary()?.branches ?? []);
  /** The month could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.entryPager.error();
    return e ? this.message(e) : null;
  });
  /** What went wrong last, in words: a refused post or reversal, else the month not read. */
  readonly error = computed(() => this.actionError() ?? this.listError());

  clearFilters(): void {
    this.openJobBranch.set(null);
    this.invoiceMatch.set('all');
    this.provisionHeld.set('all');
  }

  ngOnInit(): void {
    // The month loads itself through its pagers.
    this.chrome?.description.set(this.i18n.t('maintLedger.lead'));
  }

  /** Refresh: the entries and every list of the month on screen, asked again. */
  async reload(): Promise<void> {
    await this.entryPager.reload();
    const c = this.counts();
    await Promise.all([
      c.postedAtIssue ? this.issuePager.reload() : Promise.resolve(),
      c.openCustomerJobs ? this.openJobPager.reload() : Promise.resolve(),
      c.invoices ? this.invoicePager.reload() : Promise.resolve(),
      c.provisions ? this.provisionPager.reload() : Promise.resolve(),
    ]);
  }

  async post(): Promise<void> {
    await this.act('mfgLedger.post', (y, m) => this.api.post(y, m));
  }

  async reverse(): Promise<void> {
    await this.act('mfgLedger.reverse', (y, m) => this.api.reverse(y, m));
  }

  /** Posts or reverses the month on screen, then reads it again. */
  private async act(label: string, call: (year: number, month: number) => Promise<MaintenanceLedger>): Promise<void> {
    const d = this.data();
    if (!d || !(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t(label))))) return;
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await call(d.year, d.month);
      await this.reload();
    } catch (e) {
      this.actionError.set(this.message(e));
    } finally {
      this.busy.set(false);
    }
  }

  private message(e: unknown): string {
    const code = problemCode(e);
    return code && REFUSALS[code] ? this.i18n.t(REFUSALS[code]) : maintenanceError(e, (k) => this.i18n.t(k));
  }

  /** `yyyy-MM` as mm/yyyy, the app's way. */
  monthName(value: string): string {
    const [y, m] = value.split('-');
    return `${m}/${y}`;
  }

  /** A slot in words: maintenance's own, or the manufacturing stock slot a part left. */
  slotName(slot: string): string {
    // Phase M7c: a rework's outside work taken from the provision goes back to the account its voucher debited.
    if (slot.startsWith('account:')) return this.i18n.t('maintLedger.slot.account', { code: slot.slice('account:'.length) });
    // Phase M7d: a capitalised repair debits the REN-12 register's asset account.
    if (slot.startsWith('fixed_assets.')) return this.i18n.t('fa.slot.' + slot);
    return this.i18n.t(slot.startsWith('maintenance.') ? 'maintLedger.slot.' + slot : 'mfgChoices.slot.' + slot);
  }

  checkName(code: string): string {
    const own = ['parts_tie', 'invoices_tie', 'provision_tie', 'capitalised_tie'];
    return this.i18n.t(own.includes(code) ? 'maintLedger.check.' + code : 'mfgLedger.checkName.' + code);
  }

  /** The slot in words, and the account it names, or "not mapped". */
  accountLabel(l: PracticeLine): string {
    const slot = this.slotName(l.slot);
    if (!l.accountCode) return `${slot} — ${this.i18n.t('mfgChoices.map.unmapped')}`;
    const name = localizedName({ nameAr: l.accountNameAr ?? '', nameFr: l.accountNameFr ?? '', nameEn: l.accountNameEn ?? '' }, this.i18n.lang());
    return `${l.accountCode} ${name} (${slot})`;
  }

  branchName(e: PracticeEntry): string {
    return localizedName({ nameAr: e.branchNameAr, nameFr: e.branchNameFr, nameEn: e.branchNameEn }, this.i18n.lang()) || e.branchCode;
  }

  /** Amounts in money; the counting checks (unbalanced entries, slots mapped, invoices) as plain counts. */
  figure(code: string, value: number): string {
    return code === 'entries_balance' || code === 'accounts_mapped' || code === 'invoices_tie' ? String(value) : this.i18n.money(value);
  }
}
