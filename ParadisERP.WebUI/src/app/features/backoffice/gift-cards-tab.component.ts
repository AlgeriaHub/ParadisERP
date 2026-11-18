import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MENU } from '../../ui/menu.component';
import { AdminBranch, AdminCustomer, AdminGiftCard, AdminService, adminErrorMessage } from '../../core/admin.service';
import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { RegionalService } from '../../core/regional.service';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest } from '../../ui/confirm-requests';
import { DateInputComponent } from '../../ui/date-input.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { TableStateComponent } from '../../ui/table-state.component';
import { localizedName } from './backoffice-format';
import { SalesPagesApi } from './sales-pages.api';

/** What the adjust form offers (SAL-17); the two corrections are one server kind with a sign. */
type AdjustChoice = 'topUp' | 'cashOut' | 'correctionAdd' | 'correctionRemove';

/**
 * Issue, search, deactivate and adjust gift cards. Balances reach tills on their next pull. A card is sold for cash at a
 * branch, and a paid top-up or a cash refund goes through a branch's box (owner, 2026-11-01, SAL-17); a correction moves
 * no money. The branch is the Admin's to choose; anyone else's is their own (the server says so too).
 */
@Component({
  selector: 'app-backoffice-gift-cards',
  standalone: true,
  imports: [FormsModule, DateInputComponent, ListFooterComponent, TableStateComponent, ...MENU],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('giftCards.issue') }}</h2>
        </div>
        <div class="bo-panel__body">
          <div class="bo-fields bo-fields--roomy">
            <div class="bo-field">
              <label for="gc-code">
                {{ i18n.t('giftCards.code') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
              </label>
              <input id="gc-code" class="mono" maxlength="40" [placeholder]="i18n.t('giftCards.codeHint')" [(ngModel)]="code" />
            </div>
            <div class="bo-field" [class.is-invalid]="amountInvalid()">
              <label for="gc-amount">
                {{ i18n.t('giftCards.amount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
              </label>
              <input
                id="gc-amount"
                type="number"
                min="0"
                class="num mono"
                [step]="regional.moneyStep()"
                [(ngModel)]="amount"
                [attr.aria-invalid]="amountInvalid() ? 'true' : null"
                [attr.aria-describedby]="amountInvalid() ? 'gc-amount-error' : null"
                (blur)="amountTouched = true"
              />
              @if (amountInvalid()) {
                <span id="gc-amount-error" class="bo-error" role="alert">{{ i18n.t('giftCards.amountRequired') }}</span>
              }
            </div>
            <div class="bo-field">
              <label for="gc-expires">
                {{ i18n.t('giftCards.expires') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
              </label>
              <app-date-input id="gc-expires" [(ngModel)]="expires" />
            </div>
            <div class="bo-field">
              <label for="gc-customer">{{ i18n.t('giftCards.customer') }}</label>
              <select id="gc-customer" [(ngModel)]="customerId">
                <option [ngValue]="null">—</option>
                @for (c of customers(); track c.id) {
                  <option [ngValue]="c.id">{{ c.fullName }}</option>
                }
              </select>
            </div>
            @if (!lock.locked()) {
              <div class="bo-field">
                <label for="gc-branch">{{ i18n.t('giftCards.cash.branch') }}</label>
                <select id="gc-branch" [(ngModel)]="branchId">
                  @for (b of branches(); track b.id) {
                    <option [ngValue]="b.id">{{ branchName(b) }}</option>
                  }
                </select>
              </div>
            }
          </div>
          <p class="bo-scope-note">
            {{ i18n.t('giftCards.cash.issueNote') }}
            @if (lock.locked()) {
              {{ i18n.t('giftCards.cash.ownBranch', { branch: lock.ownBranchName(branches()) }) }}
            }
          </p>
        </div>
        <div class="bo-panel__foot">
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="issue()">
            {{ i18n.t('giftCards.issue') }}
          </button>
        </div>
      </section>

      <section class="bo-panel">
        <div class="bo-panel__head">
          <div class="ui-toolbar" role="search">
            <input
              type="search"
              class="finder"
              [placeholder]="i18n.t('giftCards.search')"
              [attr.aria-label]="i18n.t('giftCards.search')"
              [ngModel]="query()"
              (ngModelChange)="query.set($event)"
            />
            <div class="ui-filter">
              <label for="gc-f-state">{{ i18n.t('filters.status') }}</label>
              <select id="gc-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="valid">{{ i18n.t('giftCards.valid') }}</option>
                <option ngValue="expired">{{ i18n.t('giftCards.expired') }}</option>
                <option ngValue="inactive">{{ i18n.t('common.inactive') }}</option>
              </select>
            </div>
            <div class="ui-filter">
              <label for="gc-f-balance">{{ i18n.t('giftCards.balance') }}</label>
              <select id="gc-f-balance" [ngModel]="balanceFilter()" (ngModelChange)="balanceFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="left">{{ i18n.t('giftCards.filter.balanceLeft') }}</option>
                <option ngValue="spent">{{ i18n.t('giftCards.filter.spent') }}</option>
              </select>
            </div>
            <div class="ui-filter">
              <label for="gc-f-customer">{{ i18n.t('giftCards.customer') }}</label>
              <select id="gc-f-customer" [ngModel]="customerFilter()" (ngModelChange)="customerFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="linked">{{ i18n.t('giftCards.filter.linked') }}</option>
                <option ngValue="none">{{ i18n.t('giftCards.filter.anonymous') }}</option>
              </select>
            </div>
            @if (filtered()) {
              <div class="ui-toolbar__end">
                <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
              </div>
            }
          </div>
        </div>
        <div class="bo-scroll bo-scroll--tall">
          <table class="bo-data">
            <thead>
              <tr>
                <th>{{ i18n.t('giftCards.code') }}</th>
                <th class="num">{{ i18n.t('giftCards.initial') }}</th>
                <th class="num">{{ i18n.t('giftCards.balance') }}</th>
                <th>{{ i18n.t('giftCards.expires') }}</th>
                <th>{{ i18n.t('common.state') }}</th>
                <th>{{ i18n.t('giftCards.customer') }}</th>
                <th>{{ i18n.t('common.actions') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (card of pager.rows(); track card.id) {
                <tr [class.is-muted]="!card.isActive">
                  <td class="mono" [attr.data-label]="i18n.t('giftCards.code')">{{ card.code }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('giftCards.initial')">{{ i18n.money(card.initialAmount) }}</td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('giftCards.balance')">{{ i18n.money(card.balance) }}</td>
                  <td class="mono" [attr.data-label]="i18n.t('giftCards.expires')">
                    {{ card.expiresAtUtc ? card.expiresAtUtc.slice(0, 10) : '—' }}
                  </td>
                  <td [attr.data-label]="i18n.t('common.state')">
                    <span class="bo-states">
                      @if (!card.isActive) {
                        <span class="bo-state bo-state--warn">{{ i18n.t('common.inactive') }}</span>
                      } @else if (isExpired(card)) {
                        <span class="bo-state bo-state--warn">{{ i18n.t('giftCards.expired') }}</span>
                      } @else {
                        <span class="bo-state bo-state--ok">{{ i18n.t('giftCards.valid') }}</span>
                      }
                    </span>
                  </td>
                  <td [attr.data-label]="i18n.t('giftCards.customer')">{{ customerName(card.customerId) }}</td>
                  <td class="ui-cell-menu">
                    <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + card.code">
                      <span menuTrigger aria-hidden="true">⋯</span>
                      <button type="button" appMenuItem (click)="select(card)">{{ i18n.t('giftCards.history') }}</button>
                      @if (card.isActive) {
                        <hr appMenuSeparator />
                        <button type="button" appMenuItem class="is-danger" [disabled]="busy()" (click)="toggle(card)">{{ i18n.t('giftCards.deactivate') }}</button>
                      } @else {
                        <button type="button" appMenuItem [disabled]="busy()" (click)="toggle(card)">{{ i18n.t('giftCards.activate') }}</button>
                      }
                    </app-menu>
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="7" class="bo-empty">
                    @if (!pager.loaded()) {
                      <strong>{{ i18n.t('common.loading') }}</strong>
                      <span class="bo-loading__rows" aria-hidden="true"></span>
                    } @else if (filtered()) {
                      <app-table-state kind="noResults" (clear)="clearFilters()" />
                    } @else {
                      <strong>{{ i18n.t('giftCards.none') }}</strong>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
      </section>

      @if (selected(); as card) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title mono">{{ card.code }}</h2>
            <strong class="mono balance">{{ i18n.money(card.balance) }}</strong>
          </div>
          <div class="bo-panel__body">
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field">
                <label for="gc-kind">
                  {{ i18n.t('giftCards.kind') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <select id="gc-kind" [(ngModel)]="adjustKind" [attr.aria-describedby]="adjustKind ? 'gc-kind-hint' : null">
                  <option [ngValue]="null">{{ i18n.t('giftCards.kind.choose') }}</option>
                  @for (k of adjustChoices; track k) {
                    <option [ngValue]="k">{{ i18n.t('giftCards.kind.' + k) }}</option>
                  }
                </select>
                @if (adjustKind) {
                  <span id="gc-kind-hint" class="bo-hint">{{ i18n.t('giftCards.kindHint.' + (movesCash() ? adjustKind : 'correction')) }}</span>
                }
              </div>
              <div class="bo-field">
                <label for="gc-delta">
                  {{ i18n.t('giftCards.adjustAmount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input id="gc-delta" type="number" min="0" class="num mono" [step]="regional.moneyStep()" [(ngModel)]="delta" />
              </div>
              @if (movesCash() && !lock.locked()) {
                <div class="bo-field">
                  <label for="gc-adjust-branch">{{ i18n.t('giftCards.cash.branch') }}</label>
                  <select id="gc-adjust-branch" [(ngModel)]="branchId">
                    @for (b of branches(); track b.id) {
                      <option [ngValue]="b.id">{{ branchName(b) }}</option>
                    }
                  </select>
                </div>
              }
              <div class="bo-field">
                <label for="gc-reason">
                  {{ i18n.t('giftCards.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input id="gc-reason" maxlength="200" [(ngModel)]="reason" />
              </div>
            </div>
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-primary" [disabled]="busy() || !adjustKind || !(delta > 0) || !reason.trim()" (click)="adjust(card)">
              {{ i18n.t('giftCards.adjust') }}
            </button>
          </div>
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('receipt.date') }}</th>
                  <th>{{ i18n.t('giftCards.reason') }}</th>
                  <th class="num">{{ i18n.t('giftCards.delta') }}</th>
                  <th class="num">{{ i18n.t('giftCards.balance') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (m of movesPager.rows(); track m.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('receipt.date')">{{ i18n.dateTime(m.createdAtUtc) }}</td>
                    <td [attr.data-label]="i18n.t('giftCards.reason')">
                      {{ i18n.t('giftCards.reason.' + m.reason) }}{{ m.note ? ' — ' + m.note : '' }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('giftCards.delta')">
                      {{ m.delta > 0 ? '+' : '' }}{{ i18n.money(m.delta) }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('giftCards.balance')">{{ i18n.money(m.balanceAfter) }}</td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="4" class="bo-empty">
                      @if (movesPager.loading()) {
                        <strong>{{ i18n.t('common.loading') }}</strong>
                        <span class="bo-loading__rows" aria-hidden="true"></span>
                      } @else {
                        <strong>{{ i18n.t('common.nothingHere') }}</strong>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <app-list-footer [pager]="movesPager" [loading]="!movesPager.loaded()" />
        </section>
      }
    </div>
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      .finder {
        width: min(18rem, 100%);
        padding: 0.35rem 0.6rem;
        font-size: 0.86rem;
      }
      .balance {
        font-size: 1.05rem;
        font-weight: 700;
        font-variant-numeric: tabular-nums;
      }
    `,
  ],
})
export class BackofficeGiftCardsComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly pages = inject(SalesPagesApi);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly regional = inject(RegionalService);
  readonly lock = inject(BranchLock);

  readonly customers = signal<AdminCustomer[]>([]);
  /** Where a card's cash can be taken: branches, never a warehouse. */
  readonly branches = signal<AdminBranch[]>([]);
  readonly adjustChoices: AdjustChoice[] = ['topUp', 'cashOut', 'correctionAdd', 'correctionRemove'];
  readonly selected = signal<AdminGiftCard | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** The code search, as a till types it. */
  readonly query = signal('');
  // The list's filters (owner, 2026-11-17), applied by the server over every card, like the code search.
  readonly stateFilter = signal<'all' | 'valid' | 'expired' | 'inactive'>('all');
  readonly balanceFilter = signal<'all' | 'left' | 'spent'>('all');
  readonly customerFilter = signal<'all' | 'linked' | 'none'>('all');

  readonly filtered = computed(
    () => this.stateFilter() !== 'all' || this.balanceFilter() !== 'all' || this.customerFilter() !== 'all',
  );

  /**
   * The cards a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"): the code search and the filters run there, over every card; a change goes back to
   * page one. The states are the table's, decided the same way.
   */
  readonly pager = createServerPager(
    (page) => this.pages.giftCardsPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'giftCards.cards',
      filters: () => ({
        q: this.query().trim(),
        state: this.stateFilter(),
        balance: this.balanceFilter(),
        customer: this.customerFilter(),
      }),
    },
  );

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  /** One card's history from the server; its running balance pages but is never filtered. Another card starts at page one. */
  readonly movesPager = createServerPager(
    (page) => this.pages.giftCardMovementsPage(page.filters.id!, page.skip, page.take),
    {
      key: 'giftCards.movements',
      filters: () => ({ id: this.selected()?.id ?? null }),
      enabled: () => !!this.selected(),
      debounceMs: 0,
    },
  );

  clearFilters(): void {
    this.stateFilter.set('all');
    this.balanceFilter.set('all');
    this.customerFilter.set('all');
  }

  code = '';
  amount = 0;
  /** The amount only complains once the field has been left, or a card was asked for. */
  amountTouched = false;

  amountInvalid(): boolean {
    return this.amountTouched && !(Number(this.amount) > 0);
  }
  expires = '';
  customerId: string | null = null;
  /** A positive amount; the kind gives it its direction. */
  delta = 0;
  reason = '';
  /** Chosen every time (owner, 2026-11-01): there is no default kind. */
  adjustKind: AdjustChoice | null = null;
  /** The branch whose box takes or hands back the cash; a locked user's own, which the server enforces too. */
  branchId: string | null = null;

  movesCash(): boolean {
    return this.adjustKind === 'topUp' || this.adjustKind === 'cashOut';
  }

  branchName(b: AdminBranch): string {
    return localizedName(b, this.i18n.lang());
  }

  async ngOnInit(): Promise<void> {
    await this.run(async () => {
      this.customers.set(await this.admin.listCustomers());
      const branches = (await this.admin.listBranches()).filter((b) => !b.isWarehouse);
      this.branches.set(branches);
      this.branchId = this.lock.defaultBranch(branches);
      // The list loads itself through its pager.
    });
  }

  /** Asks again for the page on screen, after an issue, a change of state or an adjustment. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  customerName(id: string | null): string {
    return (id && this.customers().find((c) => c.id === id)?.fullName) || '—';
  }

  isExpired(card: AdminGiftCard): boolean {
    return !!card.expiresAtUtc && new Date(card.expiresAtUtc) <= new Date();
  }

  async issue(): Promise<void> {
    // Nothing is issued for nothing, and the button no longer greys out, so the field says why.
    this.amountTouched = true;
    if (this.amountInvalid()) return Promise.resolve();
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('giftCards.issue'))))) return;
    return this.run(async () => {
      const card = await this.admin.issueGiftCard({
        code: this.code.trim() || null,
        amount: Number(this.amount),
        // End of the chosen day, so a card is usable through its expiry date.
        expiresAtUtc: this.expires ? new Date(this.expires + 'T23:59:59Z').toISOString() : null,
        customerId: this.customerId,
        branchId: this.branchId,
      });
      this.code = '';
      this.amount = 0;
      this.amountTouched = false;
      this.expires = '';
      this.customerId = null;
      await this.reload();
      await this.select(card);
    });
  }

  toggle(card: AdminGiftCard): Promise<void> {
    return this.run(async () => {
      await this.admin.updateGiftCard(card.id, { isActive: !card.isActive, expiresAtUtc: card.expiresAtUtc, customerId: card.customerId });
      await this.reload();
    });
  }

  async adjust(card: AdminGiftCard): Promise<void> {
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('giftCards.adjust'))))) return;
    return this.run(async () => {
      const kind = this.adjustKind!;
      const amount = Math.abs(Number(this.delta));
      const updated = await this.admin.adjustGiftCard(card.id, {
        delta: kind === 'cashOut' || kind === 'correctionRemove' ? -amount : amount,
        reason: this.reason.trim(),
        kind: kind === 'topUp' || kind === 'cashOut' ? kind : 'correction',
        branchId: this.movesCash() ? this.branchId : null,
      });
      this.delta = 0;
      this.reason = '';
      this.adjustKind = null;
      await this.reload();
      await this.select(updated);
    });
  }

  /** Opens a card's history; the same card again (after an adjustment) asks for its page again. */
  async select(card: AdminGiftCard): Promise<void> {
    const same = this.selected()?.id === card.id;
    this.selected.set(card);
    if (same) await this.movesPager.reload();
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
