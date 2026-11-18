import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { localizedName } from '../../../core/multilingual';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { MonthInputComponent } from '../payroll/month-input.component';
import { parsePeriod } from '../payroll/payroll-run-view';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { CostingSettingsApi, PracticeEntry, PracticeLedger, PracticeLine } from './costing-settings-api.service';
import { ManufacturingApiService, ProductionLineSummary } from './manufacturing-api.service';
import { LedgerBranch, ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError } from './manufacturing-shared';
import { PracticeLedgerTabsComponent } from './practice-ledger-tabs.component';

/**
 * Manufacturing › Practice ledger (Phase C2, docs/manufacturing/phase-c.md): one month of the entries the admin's
 * chosen accounting model would make for production — issues, returns, releases, work in progress, and under the
 * perpetual model the absorbed labour, overhead and expenses — with each slot's total and the checks of the events
 * against the entries. Computed by the server on every read and posted nowhere. Every entry carries one order's
 * labour, so it is offered, like the cost history, only to a reader who sees pay rates.
 */
@Component({
  selector: 'app-mfg-practice-ledger',
  standalone: true,
  imports: [
    FormsModule, ...PAGE_CHROME, ListFooterComponent, MonthInputComponent, StatusComponent, TableStateComponent, PracticeLedgerTabsComponent,
  ],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="pl-month">{{ i18n.t('mfgLedger.month') }}</label>
          <app-month-input id="pl-month" [ngModel]="shownMonth()" (ngModelChange)="month.set($event)" />
        </div>
        <div class="ui-filter">
          <label for="pl-line">{{ i18n.t('mfg.boms.line') }}</label>
          <select id="pl-line" [ngModel]="lineId()" (ngModelChange)="lineId.set($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.orders.allLines') }}</option>
            @for (l of lines(); track l.id) {
              <option [ngValue]="l.id">{{ l.code }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="pl-kind">{{ i18n.t('filters.kind') }}</label>
          <select id="pl-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (k of entryKinds(); track k) {
              <option [ngValue]="k">{{ i18n.t('mfgLedger.kind.' + k) }}</option>
            }
          </select>
        </div>
        @if (entryBranches().length > 1 || branchFilter() !== 'all') {
          <div class="ui-filter">
            <label for="pl-branch">{{ i18n.t('filters.branch') }}</label>
            <select id="pl-branch" [ngModel]="branchFilter()" (ngModelChange)="branchFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (b of entryBranches(); track b.id) {
                <option [ngValue]="b.id">{{ b.name }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="busy() || entryPager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      <!-- Phase M6: maintenance's section of the practice ledger, a tab beside this one for a reader of both. -->
      <app-practice-ledger-tabs current="production" />
      @if (data(); as d) {
        <p class="notice">
          {{ i18n.t('mfgLedger.model', { model: i18n.t('mfgChoices.model.' + d.model) }) }}
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
            <strong class="ui-metric__value mono">{{ d.entryCount }}</strong>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('mfgLedger.checksPassed') }}</span>
            <strong class="ui-metric__value mono">{{ passed() }} / {{ d.checks.length }}</strong>
          </div>
        </div>

        <!-- Phase D: the accountant posts the closed month to the ledger from here, or takes it back. -->
        <section class="ui-surface pl-posting" [attr.aria-label]="i18n.t('mfgLedger.posting')">
          <h2 class="bo-panel__title">{{ i18n.t('mfgLedger.posting') }}</h2>
          @if (!d.postingFromMonth) {
            <p class="bo-hint">{{ i18n.t('mfgLedger.postingOff') }}</p>
          } @else {
            <p class="bo-hint">{{ i18n.t('mfgLedger.postingFrom', { month: monthName(d.postingFromMonth) }) }}</p>
          }
          @for (p of d.postings ?? []; track p.generation) {
            <p>
              {{ i18n.t('mfgLedger.posted', { who: p.postedBy ?? '—', date: i18n.dateTime(p.postedAtUtc), entries: p.entries, total: i18n.money(p.totalDebit) }) }}
              @if (p.reversedAtUtc) {
                · {{ i18n.t('mfgLedger.reversed', { who: p.reversedBy ?? '—', date: i18n.dateTime(p.reversedAtUtc) }) }}
              }
            </p>
          }
          @if (canPost()) {
            <div class="pl-actions">
              @if (live()) {
                <button type="button" class="btn-secondary is-danger" [disabled]="busy()" (click)="reverse()">{{ i18n.t('mfgLedger.reverse') }}</button>
              } @else if (d.postingFromMonth) {
                <button type="button" class="btn-primary" [disabled]="busy() || d.monthToDate" (click)="post()">{{ i18n.t('mfgLedger.post') }}</button>
              }
            </div>
          }
        </section>

        @if (d.unmappedSlots.length) {
          <p class="notice warn" role="status">
            {{ i18n.t('mfgLedger.unmapped', { slots: unmappedNames() }) }}
          </p>
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
                    <td [attr.data-label]="i18n.t('mfgLedger.check')">{{ i18n.t('mfgLedger.checkName.' + c.code) }}</td>
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

      <section class="ui-surface" [attr.aria-busy]="busy() || entryPager.loading()" [attr.aria-label]="i18n.t('mfgLedger.entriesTitle')">
        @if (error()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!data()) {
          <app-table-state kind="loading" />
        } @else if (!data()?.entryCount) {
          <app-table-state kind="empty" [message]="i18n.t('mfgLedger.empty')" />
        } @else if (!entryPager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
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
                      {{ i18n.t('mfgLedger.kind.' + e.kind) }}
                      <span class="ui-sub"><span class="mono" dir="ltr">{{ e.orderNumber }}</span>
                        @if (e.document) {
                          · <span class="mono" dir="ltr">{{ e.document }}</span>
                        }
                        · {{ branchName(e) }}</span>
                    </td>
                    <td [attr.data-label]="i18n.t('mfgLedger.account')">
                      @for (l of e.lines; track $index) {
                        <div class="pl-line" [class.pl-line--credit]="l.credit > 0">{{ accountLabel(l) }}</div>
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
      <app-list-footer [pager]="entryPager" [loading]="!entryPager.loaded()" />

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

      @if (data()?.lateCostCount) {
        <section class="ui-surface" [attr.aria-label]="i18n.t('mfgLedger.lateTitle')">
          @if (lateElements().length > 1 || elementFilter() !== 'all') {
            <div class="ui-toolbar pane-bar" role="search">
              <div class="ui-filter">
                <label for="pl-element">{{ i18n.t('mfgLedger.element') }}</label>
                <select id="pl-element" [ngModel]="elementFilter()" (ngModelChange)="elementFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  @for (el of lateElements(); track el) {
                    <option [ngValue]="el">{{ i18n.t('mfgLedger.element.' + el) }}</option>
                  }
                </select>
              </div>
              @if (elementFilter() !== 'all') {
                <div class="ui-toolbar__end">
                  <button type="button" class="btn-ghost" (click)="elementFilter.set('all')">{{ i18n.t('ui.state.clearFilters') }}</button>
                </div>
              }
            </div>
          }
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <caption>{{ i18n.t('mfgLedger.lateTitle') }}</caption>
              <thead>
                <tr>
                  <th>{{ i18n.t('mfgLedger.order') }}</th>
                  <th>{{ i18n.t('mfgLedger.element') }}</th>
                  <th class="num">{{ i18n.t('mfgLedger.amount') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (l of latePager.rows(); track $index) {
                  <tr>
                    <td class="mono" dir="ltr" [attr.data-label]="i18n.t('mfgLedger.order')">{{ l.orderNumber }}</td>
                    <td [attr.data-label]="i18n.t('mfgLedger.element')">{{ i18n.t('mfgLedger.element.' + l.element) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.amount')">{{ i18n.money(l.amount) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
        <app-list-footer [pager]="latePager" [loading]="!latePager.loaded()" />
      }

      @if (data(); as d) {
        <ul class="bo-hint pl-notes">
          @for (n of d.notes; track n) {
            @if (n !== 'month_to_date') {
              <li>{{ i18n.t('mfgLedger.note.' + n) }}</li>
            }
          }
        </ul>
      }
    </div>
  `,
  styles: [
    `
      .pl-line--credit {
        padding-inline-start: var(--sp-4);
      }
      .pl-posting {
        padding: var(--sp-4);
        display: grid;
        gap: var(--sp-2);
      }
      .pl-posting p {
        margin: 0;
      }
      .pl-actions {
        display: flex;
        justify-content: flex-end;
      }
      .pl-notes {
        margin: 0;
        padding-inline-start: var(--sp-4);
      }
      /* The late costs' filter, inside the card's own edges. */
      .pane-bar {
        padding-block: var(--sp-3);
        padding-inline: var(--sp-4);
      }
    `,
  ],
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MfgPracticeLedgerComponent implements OnInit {
  private readonly costing = inject(CostingSettingsApi);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly api = inject(ManufacturingApiService);
  private readonly chrome = inject(PageChrome, { optional: true });
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  /** Posting and reversing are the accountant's: accounting.manage, checked again by the server. */
  readonly canPost = computed(() => mayDo(this.auth.currentUser(), 'accounting.manage'));
  /** The month's posting that stands (not reversed), if any. */
  readonly live = computed(() => (this.data()?.postings ?? []).some((p) => !p.reversedAtUtc));
  readonly toneOf = toneOf;

  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly lineId = signal<string | null>(null);
  /**
   * `YYYY-MM` as the reader chose it. Empty until they choose: the server answers the company's month today, and the
   * box shows the month it answered (`shownMonth`) without asking again.
   */
  readonly month = signal('');
  /** Posting or reversing the month. */
  readonly busy = signal(false);
  /** Posting or reversing was refused, in words. */
  readonly actionError = signal<string | null>(null);

  /** The month the pages ask for: the one chosen, or (null) the company's month today. */
  private readonly period = computed(() => {
    const p = parsePeriod(this.month());
    return { year: p?.year ?? null, month: p?.month ?? null };
  });

  // The entries' kind and branch, filtered on the server. The month and the line are the ledger's own.
  readonly kindFilter = signal<string>('all');
  readonly branchFilter = signal<string>('all');
  readonly filtered = computed(() => this.kindFilter() !== 'all' || this.branchFilter() !== 'all');
  /**
   * The month's entries a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend").
   * The summary is the month without its entries and late costs — model, tiles, posting, checks, accounts, notes —
   * and the kinds and branches its entries name.
   */
  readonly entryPager = createServerPager(
    (page) => this.pages.ledgerEntries({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.ledger.entries',
      filters: () => ({
        ...this.period(),
        lineId: this.lineId(),
        kind: this.kindFilter(),
        entryBranchId: this.branchFilter() === 'all' ? undefined : this.branchFilter(),
      }),
    },
  );
  /** The month as the server answered it, without its entries; null until then, and when it was refused. */
  readonly data = computed(() => (this.entryPager.error() ? null : this.entryPager.summary()));
  /** The month on screen: the one chosen, else the one the server answered. */
  readonly shownMonth = computed(() => {
    const d = this.entryPager.summary();
    return this.month() || (d ? `${d.year}-${String(d.month).padStart(2, '0')}` : '');
  });

  readonly passed = computed(() => this.data()?.checks.filter((c) => c.passed).length ?? 0);
  /** The kinds the month holds, in the order they first appear. */
  readonly entryKinds = computed(() => this.data()?.kinds ?? []);
  /** The branches the month's entries name, each once. */
  readonly entryBranches = computed(() => (this.data()?.branches ?? []).map((b) => ({ id: b.id, name: this.ledgerBranchName(b) })));

  // The late costs, by element, from the server too — asked only when the month holds some.
  readonly elementFilter = signal<string>('all');
  readonly latePager = createServerPager(
    (page) => this.pages.ledgerLateCosts({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.ledger.late',
      filters: () => ({ ...this.period(), lineId: this.lineId(), element: this.elementFilter() }),
      enabled: () => !!this.data()?.lateCostCount,
    },
  );
  readonly lateElements = computed(() => this.latePager.summary()?.choices ?? []);

  /** The month (or posting it) could not be read, in words. */
  readonly error = computed(() => {
    const e = this.entryPager.error();
    return this.actionError() ?? (e ? mfgError(e, (k) => this.i18n.t(k)) : null);
  });

  clearFilters(): void {
    this.kindFilter.set('all');
    this.branchFilter.set('all');
  }
  readonly unmappedNames = computed(() =>
    (this.data()?.unmappedSlots ?? []).map((s) => this.i18n.t('mfgChoices.slot.' + s)).join(' · '),
  );

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfgLedger.lead'));
    this.api.lines(true).then((l) => this.lines.set(l), () => undefined);
  }

  /** Asks again for the month: its entries' page and, when it holds some, its late costs' page. */
  async reload(): Promise<void> {
    this.actionError.set(null);
    await Promise.all([this.entryPager.reload(), this.data()?.lateCostCount ? this.latePager.reload() : Promise.resolve()]);
  }

  async post(): Promise<void> {
    await this.act('mfgLedger.post', (y, m) => this.costing.postMonth(y, m));
  }

  async reverse(): Promise<void> {
    await this.act('mfgLedger.reverse', (y, m) => this.costing.reverseMonth(y, m));
  }

  private async act(label: string, call: (year: number, month: number) => Promise<PracticeLedger>): Promise<void> {
    const d = this.data();
    if (!d || !(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t(label))))) return;
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await call(d.year, d.month);
      // The month as the server now holds it: its posting, and the page of entries on screen.
      await this.entryPager.reload();
    } catch (e) {
      this.actionError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  /** `yyyy-MM` as mm/yyyy, the app's way. */
  monthName(value: string): string {
    const [y, m] = value.split('-');
    return `${m}/${y}`;
  }

  /** The slot in words, and the account it names, or "not mapped". */
  accountLabel(l: PracticeLine): string {
    const slot = this.i18n.t('mfgChoices.slot.' + l.slot);
    if (!l.accountCode) return `${slot} — ${this.i18n.t('mfgChoices.map.unmapped')}`;
    const name = localizedName({ nameAr: l.accountNameAr ?? '', nameFr: l.accountNameFr ?? '', nameEn: l.accountNameEn ?? '' }, this.i18n.lang());
    return `${l.accountCode} ${name} (${slot})`;
  }

  branchName(e: PracticeEntry): string {
    return localizedName({ nameAr: e.branchNameAr, nameFr: e.branchNameFr, nameEn: e.branchNameEn }, this.i18n.lang()) || e.branchCode;
  }

  /** A branch the entries name, as the branch filter offers it — the same words as the entry's own line. */
  ledgerBranchName(b: LedgerBranch): string {
    return localizedName({ nameAr: b.nameAr, nameFr: b.nameFr, nameEn: b.nameEn }, this.i18n.lang()) || b.code;
  }

  /** Amounts in money; the two counting checks (unbalanced entries, slots mapped) as plain counts. */
  figure(code: string, value: number): string {
    return code === 'entries_balance' || code === 'accounts_mapped' ? String(value) : this.i18n.money(value);
  }
}
