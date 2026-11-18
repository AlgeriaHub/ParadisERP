import { Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminCashBox, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { BranchFace } from '../../../core/branch-face';
import { BranchLock } from '../../../core/branch-lock';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { formatDay, localizedName } from '../backoffice-format';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { cashBoxesFor, cashFigures, remittanceDefaults, safesElsewhere } from './cash-boxes';
import { FinancePagesApi } from './finance-pages.api';

/** Where the page is: the boxes, recording a remittance, or one box's movements. */
export type CashView = { kind: 'boxes' } | { kind: 'remit' } | { kind: 'box'; id: string };

const BASE = ['/backoffice', 'finance', 'cash-boxes'];

/**
 * Finance › Cash boxes: where the cash physically is, what moved, and the manager collecting a
 * branch's takings (a remittance).
 *
 * Every view is an address (`cash-boxes`, `…/remit`, `…/<box id>`), so a reload, a deep link,
 * Back and Forward land where the address says.
 *
 * Whose boxes: the working branch for the Admin (D2) — every branch's, or one branch's — and a
 * locked user's own. On one branch only that branch's boxes and that branch's total are shown; the
 * central safe of another branch is named as where takings go, never with a figure, because the
 * server does not give one (`TreasuryEndpoints` /cash-boxes) and a company total must not read as
 * a branch's.
 */
@Component({
  selector: 'app-cash-boxes',
  standalone: true,
  imports: [FormsModule, RouterLink, ...PAGE_CHROME, ...MENU, ListFooterComponent, TableStateComponent],
  template: `
    @if (view().kind === 'boxes') {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="open(REMIT_URL)">
          <span aria-hidden="true">+</span> {{ i18n.t('treasury.newRemittance') }}
        </button>
      </ng-template>
      @if (scope.canChoose()) {
        <ng-template appPageToolbar>
          <div class="ui-toolbar">
            <div class="ui-filter">
              <label for="cb-branch">{{ i18n.t('branchLock.branch') }}</label>
              <select id="cb-branch" [ngModel]="branchId()" (ngModelChange)="pickBranch($event)">
                <option [ngValue]="null">{{ i18n.t('pv.list.allBranches') }}</option>
                @for (b of face.all(); track b.id) {
                  @if (b.isActive) {
                    <option [ngValue]="b.id">{{ name(b) }}</option>
                  }
                }
              </select>
            </div>
            <div class="ui-toolbar__end">
              <button type="button" class="btn-ghost" [disabled]="busy()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
            </div>
          </div>
        </ng-template>
      }
    }

    <div class="bo-stack">
      @if (error() || movementError()) {
        <p class="notice danger" role="alert">{{ error() || movementError() }}</p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }
      @if (remittanceWarning()) {
        <p class="notice warn" role="status">{{ remittanceWarning() }}</p>
      }

      @switch (view().kind) {
        @case ('boxes') {
          @if (shown().length) {
            @switch (figures().scope) {
              @case ('company') {
                <div class="ui-metrics">
                  <div class="ui-metric ui-metric--lead">
                    <span class="ui-metric__label">{{ i18n.t('treasury.cashTotal') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(figures().total) }}</strong>
                    <span class="ui-metric__note">{{ i18n.t('treasury.boxCount') }} <span class="mono">{{ figures().boxes }}</span></span>
                  </div>
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('treasury.centralSafe') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(figures().central ?? 0) }}</strong>
                  </div>
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('treasury.atBranches') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(atBranches()) }}</strong>
                    <span class="ui-metric__note">{{ i18n.t('treasury.awaitingRemittance') }}</span>
                  </div>
                </div>
              }
              @case ('branch') {
                <div class="ui-metrics">
                  <div class="ui-metric ui-metric--lead">
                    <span class="ui-metric__label">{{ i18n.t('fin.cash.atThisBranch') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(figures().total) }}</strong>
                    <span class="ui-metric__note">{{ i18n.t('treasury.boxCount') }} <span class="mono">{{ figures().boxes }}</span></span>
                  </div>
                  @if (figures().central !== null) {
                    <div class="ui-metric">
                      <span class="ui-metric__label">{{ i18n.t('treasury.centralSafe') }}</span>
                      <strong class="ui-metric__value mono">{{ i18n.money(figures().central ?? 0) }}</strong>
                    </div>
                  }
                </div>
              }
            }
          }

          <section class="ui-surface" [attr.aria-busy]="busy() && !shown().length">
            <div class="ui-surface__head">
              <p>{{ i18n.t('treasury.cashHint') }}</p>
            </div>
            @if (busy() && !shown().length) {
              <app-table-state kind="loading" />
            } @else if (!shown().length) {
              <app-table-state kind="empty" [message]="i18n.t('treasury.noBoxes')" />
            } @else {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('treasury.cashBox') }}</th>
                      @if (branchId() === null) {
                        <th>{{ i18n.t('branchLock.branch') }}</th>
                      }
                      <th class="num">{{ i18n.t('treasury.balance') }}</th>
                      <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (b of shown(); track b.id) {
                      <tr>
                        <td [attr.data-label]="i18n.t('treasury.cashBox')">
                          <a class="ui-link" [routerLink]="boxLink(b.id)" dir="auto">{{ boxName(b) }}</a>
                          @if (b.isMain) {
                            <span class="ui-tag">{{ i18n.t('treasury.centralSafe') }}</span>
                          }
                        </td>
                        @if (branchId() === null) {
                          <td [attr.data-label]="i18n.t('branchLock.branch')">{{ face.nameOf(b.branchId) || '—' }}</td>
                        }
                        <td class="num mono figure" [attr.data-label]="i18n.t('treasury.balance')">{{ i18n.money(b.balance) }}</td>
                        <td class="ui-cell-menu">
                          <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + boxName(b)">
                            <span menuTrigger aria-hidden="true">⋯</span>
                            <a appMenuItem [routerLink]="boxLink(b.id)">{{ i18n.t('treasury.movements') }}</a>
                            <a appMenuItem [routerLink]="REMIT_URL" [queryParams]="{ from: b.id }">{{ i18n.t('treasury.newRemittance') }}</a>
                          </app-menu>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>

          @if (elsewhere().length) {
            <p class="ui-sub">
              {{ i18n.t('fin.cash.safeElsewhere', { safe: elsewhereNames() }) }}
            </p>
          }
        }

        @case ('remit') {
          <section class="ui-surface pad">
            <p class="ui-sub">{{ i18n.t('treasury.remittanceHint') }}</p>
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field">
                <label for="tr-from">{{ i18n.t('treasury.from') }}</label>
                <select id="tr-from" [(ngModel)]="remittance.fromCashBoxId">
                  @for (b of sources(); track b.id) {
                    <option [ngValue]="b.id">{{ boxName(b) }}</option>
                  }
                </select>
              </div>
              <div class="bo-field" [class.is-invalid]="sameBox()">
                <label for="tr-to">{{ i18n.t('treasury.to') }}</label>
                <select
                  id="tr-to"
                  [(ngModel)]="remittance.toCashBoxId"
                  [attr.aria-invalid]="sameBox() ? 'true' : null"
                  [attr.aria-describedby]="sameBox() ? 'tr-to-error' : null"
                >
                  @for (b of destinations(); track b.id) {
                    <option [ngValue]="b.id">{{ boxName(b) }}</option>
                  }
                </select>
                @if (sameBox()) {
                  <span id="tr-to-error" class="bo-error" role="alert">{{ i18n.t('treasury.sameBox') }}</span>
                }
              </div>
              <p class="ui-sub">{{ i18n.t('treasury.balance') }}: {{ i18n.money(sourceBalance()) }}</p>
              <div class="bo-field" [class.is-invalid]="exceedsBalance() || (amountTouched && !remittedAmount())">
                <label for="tr-amount">
                  {{ i18n.t('pay.amount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input
                  id="tr-amount"
                  type="number"
                  min="0"
                  class="num mono"
                  [(ngModel)]="remittance.amount"
                  [attr.aria-invalid]="exceedsBalance() || (amountTouched && !remittedAmount()) ? 'true' : null"
                  [attr.aria-describedby]="exceedsBalance() ? 'tr-balance-error' : (amountTouched && !remittedAmount() ? 'tr-amount-error' : null)"
                  (blur)="amountTouched = true"
                />
                @if (exceedsBalance()) {
                  <span id="tr-balance-error" class="bo-error" role="alert">{{ i18n.t('treasury.insufficientBalance') }}</span>
                }
                @if (amountTouched && !remittedAmount()) {
                  <span id="tr-amount-error" class="bo-error" role="alert">{{ i18n.t('treasury.amountRequired') }}</span>
                }
              </div>
              <div class="bo-field">
                <label for="tr-ref">
                  {{ i18n.t('treasury.reference') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input id="tr-ref" [(ngModel)]="remittance.reference" />
              </div>
            </div>
          </section>
          <div class="ui-actionbar">
            <button type="button" class="btn-ghost" (click)="open(BASE)">{{ i18n.t('common.cancel') }}</button>
            <button type="button" class="btn-primary" [disabled]="busy() || sameBox() || exceedsBalance() || !remittedAmount()" (click)="remit()">
              {{ i18n.t('treasury.recordRemittance') }}
            </button>
          </div>
        }

        @case ('box') {
          <section class="ui-surface" [attr.aria-busy]="movementsLoading()">
            @if (movementsLoading()) {
              <app-table-state kind="loading" />
            } @else if (!movementPager.total()) {
              <app-table-state kind="empty" [message]="i18n.t('treasury.noMovements')" />
            } @else {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('treasury.date') }}</th>
                      <th>{{ i18n.t('treasury.kind') }}</th>
                      <th>{{ i18n.t('treasury.direction') }}</th>
                      <th class="num">{{ i18n.t('pay.amount') }}</th>
                      <th class="num">{{ i18n.t('treasury.balance') }}</th>
                      <th>{{ i18n.t('treasury.by') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (m of movementPager.rows(); track m.id) {
                      <tr>
                        <td class="mono" [attr.data-label]="i18n.t('treasury.date')">{{ day(m.occurredAtUtc) }}</td>
                        <td [attr.data-label]="i18n.t('treasury.kind')">{{ i18n.t('treasury.kind.' + m.kind.toLowerCase()) }}</td>
                        <td [attr.data-label]="i18n.t('treasury.direction')">{{ i18n.t(m.direction === 'Out' ? 'treasury.out' : 'treasury.in') }}</td>
                        <td class="num mono" [class.out]="m.direction === 'Out'" [attr.data-label]="i18n.t('pay.amount')">
                          {{ m.direction === 'Out' ? '−' : '+' }}{{ i18n.money(m.amount) }}
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('treasury.balance')">{{ i18n.money(m.balanceAfter) }}</td>
                        <td [attr.data-label]="i18n.t('treasury.by')">{{ m.userName || '—' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
          <!-- Paged only (owner, 2026-11-17): each row carries the balance after it, which a filter would skip. -->
          <app-list-footer [pager]="movementPager" [loading]="movementsLoading()" />
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .figure {
        font-weight: 700;
      }
      .pad {
        display: grid;
        gap: var(--sp-4);
        padding: var(--sp-5);
      }
      .ui-tag {
        margin-inline-start: var(--sp-2);
      }
      /* Money that left the box carries a minus in front of it as well as this hue, so the
         direction survives a monochrome print and a colour-blind reader. */
      .out {
        color: var(--danger);
      }
    `,
  ],
})
export class CashBoxesComponent implements OnInit {
  readonly i18n = inject(I18nService);
  readonly scope = inject(BranchScopeService);
  readonly face = inject(BranchFace);
  private readonly lock = inject(BranchLock);
  private readonly admin = inject(AdminService);
  private readonly regional = inject(RegionalService);
  private readonly router = inject(Router);
  private readonly chrome = inject(PageChrome, { optional: true });
  private readonly confirm = inject(ConfirmService);

  readonly BASE = BASE;
  readonly REMIT_URL = [...BASE, 'remit'];

  /** The address under `finance/cash-boxes` (core/nav.ts `deep`). */
  readonly path = input<string[]>([]);
  readonly view = computed<CashView>(() => cashViewFor(this.path()));

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly remittanceWarning = signal<string | null>(null);

  /** The boxes of the scope, as the server answered for it. */
  readonly boxes = signal<AdminCashBox[]>([]);
  /** Every box this reader may name: the Admin's all, a locked user's own and the safe. */
  readonly allBoxes = signal<AdminCashBox[]>([]);
  private readonly pages = inject(FinancePagesApi);
  /** The box whose movements are open, from the address; null on the other views. */
  readonly boxId = computed(() => {
    const view = this.view();
    return view.kind === 'box' ? view.id : null;
  });
  /**
   * A box's movements a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"), newest
   * first, each with the balance after it; ten to begin with, another box starts at its first page.
   */
  readonly movementPager = createServerPager(
    (page) => this.pages.cashMovementsPage(page.filters.id!, { skip: page.skip, take: page.take }),
    { key: 'finance.cashMovements', filters: () => ({ id: this.boxId() }), enabled: () => this.boxId() !== null },
  );
  /** Until the open box's first page has come back: another box's rows are not shown under this one's name. */
  readonly movementsLoading = computed(() => {
    const summary = this.movementPager.summary();
    const shown = summary?.cashBoxId?.toLowerCase();
    return !this.movementPager.loaded() || (!this.movementPager.error() && shown !== this.boxId()?.toLowerCase());
  });
  readonly movementError = computed(() => {
    const e = this.boxId() ? this.movementPager.error() : null;
    return e ? adminErrorMessage(e) : null;
  });

  readonly branchId = computed(() => this.scope.resolve('branchOrAll').branchId);
  readonly shown = computed(() => cashBoxesFor(this.boxes(), this.branchId()));
  readonly elsewhere = computed(() => safesElsewhere(this.boxes(), this.branchId()));
  readonly figures = computed(() => cashFigures(this.shown(), this.branchId()));
  readonly atBranches = computed(() => {
    const f = this.figures();
    return f.scope === 'company' ? f.atBranches : 0;
  });

  /** A remittance is taken from a box this reader holds (the server refuses any other) to any box named. */
  readonly sources = computed(() => this.lock.scopeRows(this.allBoxes()));
  readonly destinations = this.allBoxes;

  amountTouched = false;
  remittance = { fromCashBoxId: '', toCashBoxId: '', amount: 0, reference: '' };

  constructor() {
    // The boxes of the scope: on arrival, and whenever the branch moves here or in the header.
    effect(() => {
      this.branchId();
      untracked(() => void this.reload());
    });
    // Each view names itself; a box's movements load when its address is reached.
    effect(() => {
      const view = this.view();
      this.allBoxes();
      untracked(() => {
        // What a remittance said belongs on the boxes it lands on; reaching anything else drops it.
        if (view.kind !== 'boxes') {
          this.done.set(null);
          this.remittanceWarning.set(null);
        }
        if (view.kind === 'boxes') this.setHeading(null, null);
        if (view.kind === 'remit') this.setHeading(this.i18n.t('treasury.newRemittance'), this.i18n.t('treasury.remittanceHint'));
        if (view.kind === 'box') {
          const box = this.allBoxes().find((b) => b.id === view.id) ?? this.boxes().find((b) => b.id === view.id);
          this.setHeading(box ? this.boxName(box) : this.i18n.t('treasury.movements'), this.i18n.t('treasury.movements'));
        }
      });
    });
    // A box's movements load through their pager when its address is reached.
    effect(() => {
      const view = this.view();
      if (view.kind === 'remit') untracked(() => this.prefillRemittance());
    });
  }

  async ngOnInit(): Promise<void> {
    try {
      this.allBoxes.set(await this.admin.listCashBoxes());
      if (this.view().kind === 'remit') this.prefillRemittance();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  pickBranch(id: string | null): void {
    this.scope.set(id ? { kind: 'branch', id } : { kind: 'all' });
  }

  name(branch: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(branch, this.i18n.lang());
  }

  boxName(b: AdminCashBox): string {
    return localizedName(b, this.i18n.lang());
  }

  open(url: string[]): void {
    void this.router.navigate(url);
  }

  boxLink(id: string): string[] {
    return [...BASE, id];
  }

  elsewhereNames(): string {
    return this.elsewhere()
      .map((b) => this.boxName(b))
      .join(', ');
  }

  /** Date only, as the treasury list showed it. */
  day(value: string | null): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  sourceBalance(): number {
    return this.sources().find((box) => box.id === this.remittance.fromCashBoxId)?.balance ?? 0;
  }

  exceedsBalance(): boolean {
    return this.remittedAmount() > this.sourceBalance();
  }

  remittedAmount(): number {
    return Math.max(0, Number(this.remittance.amount) || 0);
  }

  /** Cash cannot move to the box it is already in; refused here rather than by the server. */
  sameBox(): boolean {
    return !!this.remittance.fromCashBoxId && this.remittance.fromCashBoxId === this.remittance.toCashBoxId;
  }

  async reload(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.boxes.set(await this.admin.listCashBoxes(this.branchId()));
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async remit(): Promise<void> {
    this.amountTouched = true;
    if (this.busy() || this.sameBox() || !this.remittedAmount() || this.exceedsBalance()) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('treasury.recordRemittance'))))) return;
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    this.remittanceWarning.set(null);
    try {
      const result = await this.admin.remit({
        fromCashBoxId: this.remittance.fromCashBoxId,
        toCashBoxId: this.remittance.toCashBoxId,
        amount: Number(this.remittance.amount) || 0,
        reference: this.remittance.reference || null,
        note: null,
      });
      this.remittance.amount = 0;
      this.remittance.reference = '';
      this.amountTouched = false;
      this.allBoxes.set(await this.admin.listCashBoxes());
      // In place of the form: Back from the boxes does not reopen a form already recorded.
      await this.router.navigate(BASE, { replaceUrl: true });
      // The server rechecks the current balance before recording the transfer.
      this.done.set(this.i18n.t('fin.cash.remitted'));
      this.remittanceWarning.set(result?.warning ?? null);
      await this.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  private prefillRemittance(): void {
    const from = new URLSearchParams(this.router.url.split('?')[1] ?? '').get('from');
    const defaults = remittanceDefaults(this.sources(), this.destinations(), this.branchId());
    this.remittance.fromCashBoxId = from && this.sources().some((b) => b.id === from) ? from : defaults.from;
    this.remittance.toCashBoxId ||= defaults.to;
    this.done.set(null);
  }

  private setHeading(heading: string | null, description: string | null): void {
    this.chrome?.heading.set(heading);
    this.chrome?.description.set(description);
    this.chrome?.trail.set([]);
  }
}

/** The view an address under `finance/cash-boxes` names; anything unrecognised is the boxes. */
export function cashViewFor(path: readonly string[]): CashView {
  const [first] = path;
  if (!first || path.length > 1) return { kind: 'boxes' };
  if (first === 'remit') return { kind: 'remit' };
  return { kind: 'box', id: first };
}
