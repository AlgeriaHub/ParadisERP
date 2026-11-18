import { ChangeDetectionStrategy, Component, computed, effect, inject, input, model, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminService, OpeningDirection } from '../../core/admin.service';
import { AuthService } from '../../core/auth.service';
import { adminErrorMessage } from '../../core/error-message';
import { I18nService } from '../../core/i18n.service';
import { localToday } from '../../core/identity';
import {
  OpeningDraft,
  OpeningParty,
  canSetOpeningBalance,
  canSetSupplierOpeningBalance,
  emptyOpeningDraft,
  isOpeningSet,
  openingDirectionOf,
  openingDraftProblem,
  openingPayload,
} from '../../core/opening-balance';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest } from '../../ui/confirm-requests';
import { StatusComponent } from '../../ui/status.component';
import { DateInputComponent } from '../../ui/date-input.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { toneOf } from '../../ui/status-tones';
import { formatDay } from './backoffice-format';
import type { PageResult } from '../../core/paging';
import { SalesPagesApi } from './sales-pages.api';
import { SuppliersApiService } from './suppliers/suppliers-api.service';

export interface OpeningPartyCorrection {
  id: string;
  number: string;
  amount: number;
  effectiveOn: string;
  reason: string;
  createdBy: string | null;
  createdAtUtc: string;
}

/**
 * An opening balance inside the customer/supplier editor (owner, 2026-10-10, go-live).
 *
 * <p>Three states, all decided by the server and only read here:</p>
 * <ul>
 *   <li><b>New party</b> (`party` null): optional inputs bound to `draft`; the parent sends
 *       them with the create. Blank or zero is "owes nothing" and sends nothing.</li>
 *   <li><b>Not set</b>: whoever holds the capability may set it here, once — its own
 *       call, not the editor's save, so a cancelled edit never half-writes money.</li>
 *   <li><b>Set</b>: fixed. The figure, who set it and every correction show; a correction with its
 *       reason is the only change, and only for the same capability.</li>
 * </ul>
 */
@Component({
  selector: 'app-customer-opening-balance',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, StatusComponent, DateInputComponent, ListFooterComponent],
  template: `
    @if (!party()) {
      <p class="bo-group__note">{{ i18n.t(k('lead')) }}</p>
      <div class="bo-fields">
        <div class="bo-field" [class.is-invalid]="!!draftProblem()">
          <label for="ob-amount">
            {{ i18n.t('cust.opening.amount') }} <span class="bo-req">{{ i18n.t('common.optional') }}</span>
          </label>
          <input
            id="ob-amount" type="number" min="0" step="any" class="num mono" dir="ltr"
            [ngModel]="draft().amount" (ngModelChange)="patchDraft({ amount: $event })"
            [attr.aria-invalid]="draftProblem() ? 'true' : null"
            [attr.aria-describedby]="draftProblem() ? 'ob-amount-error' : null"
          />
          @if (draftProblem()) {
            <span id="ob-amount-error" class="bo-error" role="alert">{{ i18n.t(draftProblem()!) }}</span>
          }
        </div>
        <div class="bo-field">
          <label for="ob-direction">{{ i18n.t('cust.opening.direction') }}</label>
          <select id="ob-direction" [ngModel]="draft().direction" (ngModelChange)="patchDraft({ direction: $event })">
            <option value="owesUs">{{ i18n.t(k('owesUs')) }}</option>
            <option value="weOwe">{{ i18n.t(k('weOwe')) }}</option>
          </select>
        </div>
        <div class="bo-field">
          <label for="ob-on">{{ i18n.t('cust.opening.on') }}</label>
          <app-date-input id="ob-on" [max]="today" [ngModel]="draft().on" (ngModelChange)="patchDraft({ on: $event })" />
        </div>
      </div>
    } @else {
      @let c = party()!;
      @if (isSet(c)) {
        <div class="ob-summary">
          <app-status [tone]="tone('set')">{{ i18n.t('cust.opening.state.set') }}</app-status>
          <strong class="mono num">{{ i18n.money(abs(c.openingBalance ?? 0)) }}</strong>
          <span>{{ i18n.t(k(directionOf(c.openingBalance ?? 0))) }}</span>
        </div>
        <p class="bo-hint">
          {{ i18n.t('cust.opening.setBy', { day: day(c.openingBalanceOn), by: c.openingBalanceSetBy ?? '—' }) }}
        </p>
        @if (correctionsShown()) {
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('cust.opening.correction.number') }}</th>
                  <th>{{ i18n.t('cust.opening.on') }}</th>
                  <th class="num">{{ i18n.t('cust.opening.amount') }}</th>
                  <th>{{ i18n.t('cust.opening.direction') }}</th>
                  <th>{{ i18n.t('cust.opening.correction.reason') }}</th>
                  <th>{{ i18n.t('cust.opening.correction.by') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (x of correctionsPager.rows(); track x.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('cust.opening.correction.number')">{{ x.number }}</td>
                    <td [attr.data-label]="i18n.t('cust.opening.on')">{{ day(x.effectiveOn) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('cust.opening.amount')">{{ i18n.money(abs(x.amount)) }}</td>
                    <td [attr.data-label]="i18n.t('cust.opening.direction')">{{ i18n.t(k('correction.' + directionOf(x.amount))) }}</td>
                    <td [attr.data-label]="i18n.t('cust.opening.correction.reason')">{{ x.reason }}</td>
                    <td [attr.data-label]="i18n.t('cust.opening.correction.by')">{{ x.createdBy ?? '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <app-list-footer [pager]="correctionsPager" />
        }
        @if (mayAct()) {
          @if (!correcting()) {
            <button type="button" class="btn-ghost ob-correct" (click)="startCorrection()">{{ i18n.t('cust.opening.correct') }}</button>
          } @else {
            <p class="bo-group__note">{{ i18n.t('cust.opening.correction.lead') }}</p>
            <div class="bo-fields">
              <div class="bo-field" [class.is-invalid]="!!actionProblem()">
                <label for="obc-amount">{{ i18n.t('cust.opening.amount') }}</label>
                <input id="obc-amount" type="number" min="0" step="any" class="num mono" dir="ltr"
                  [ngModel]="action().amount" (ngModelChange)="patchAction({ amount: $event })" />
              </div>
              <div class="bo-field">
                <label for="obc-direction">{{ i18n.t('cust.opening.direction') }}</label>
                <select id="obc-direction" [ngModel]="action().direction" (ngModelChange)="patchAction({ direction: $event })">
                  <option value="owesUs">{{ i18n.t(k('correction.owesUs')) }}</option>
                  <option value="weOwe">{{ i18n.t(k('correction.weOwe')) }}</option>
                </select>
              </div>
              <div class="bo-field">
                <label for="obc-on">{{ i18n.t('cust.opening.on') }}</label>
                <app-date-input id="obc-on" [max]="today" [ngModel]="action().on" (ngModelChange)="patchAction({ on: $event })" />
              </div>
              <div class="bo-field bo-field--wide" [class.is-invalid]="reasonMissing()">
                <label for="obc-reason">{{ i18n.t('cust.opening.correction.reason') }} <span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <textarea id="obc-reason" rows="2" maxlength="400" [ngModel]="reason()" (ngModelChange)="reason.set($event)"></textarea>
              </div>
            </div>
            @if (actionProblem()) {
              <span class="bo-error" role="alert">{{ i18n.t(actionProblem()!) }}</span>
            }
            <div class="ob-actions">
              <button type="button" class="btn-ghost" (click)="correcting.set(false)">{{ i18n.t('common.cancel') }}</button>
              <button type="button" class="btn-primary" [disabled]="busy()" (click)="saveCorrection(c)">
                {{ i18n.t('cust.opening.correction.save') }}
              </button>
            </div>
          }
        }
      } @else {
        <div class="ob-summary">
          <app-status [tone]="tone('notSet')">{{ i18n.t('cust.opening.state.notSet') }}</app-status>
        </div>
        @if (mayAct()) {
          <p class="bo-group__note">{{ i18n.t(k('setLater')) }}</p>
          <div class="bo-fields">
            <div class="bo-field" [class.is-invalid]="!!actionProblem()">
              <label for="obs-amount">{{ i18n.t('cust.opening.amount') }}</label>
              <input id="obs-amount" type="number" min="0" step="any" class="num mono" dir="ltr"
                [ngModel]="action().amount" (ngModelChange)="patchAction({ amount: $event })" />
            </div>
            <div class="bo-field">
              <label for="obs-direction">{{ i18n.t('cust.opening.direction') }}</label>
              <select id="obs-direction" [ngModel]="action().direction" (ngModelChange)="patchAction({ direction: $event })">
                <option value="owesUs">{{ i18n.t(k('owesUs')) }}</option>
                <option value="weOwe">{{ i18n.t(k('weOwe')) }}</option>
              </select>
            </div>
            <div class="bo-field">
              <label for="obs-on">{{ i18n.t('cust.opening.on') }}</label>
              <app-date-input id="obs-on" [max]="today" [ngModel]="action().on" (ngModelChange)="patchAction({ on: $event })" />
            </div>
          </div>
          @if (actionProblem()) {
            <span class="bo-error" role="alert">{{ i18n.t(actionProblem()!) }}</span>
          }
          <div class="ob-actions">
            <button type="button" class="btn-primary" [disabled]="busy()" (click)="setNow(c)">{{ i18n.t('cust.opening.set') }}</button>
          </div>
        } @else {
          <p class="bo-hint">{{ i18n.t(k('adminOnly')) }}</p>
        }
      }
      @if (error()) {
        <p class="bo-error" role="alert">{{ error() }}</p>
      }
    }
  `,
  styles: [
    `
      :host { display: block; }
      .ob-summary {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-3);
        margin-block-end: var(--sp-2);
      }
      .ob-correct { margin-block-start: var(--sp-3); }
      .ob-actions {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        gap: var(--sp-2);
        margin-block-start: var(--sp-3);
      }
    `,
  ],
})
export class CustomerOpeningBalanceComponent {
  private readonly admin = inject(AdminService);
  private readonly suppliersApi = inject(SuppliersApiService);
  private readonly pages = inject(SalesPagesApi);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);

  /** Null while the party is being created; the row being edited otherwise. */
  readonly party = input<OpeningParty | null>(null);
  readonly kind = input<'customer' | 'supplier'>('customer');
  /** The create form's inputs, read by the parent when it saves. */
  readonly draft = model<OpeningDraft>(emptyOpeningDraft(localToday()));
  /** The party as the server answered after a set or a correction, for the parent to show. */
  readonly changed = output<OpeningParty | null>();

  readonly today = localToday();
  /**
   * The corrections, ten to a page, from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"); read only once the opening balance is set. Another party starts at page one. Each
   * answer is tagged with the party it was asked for, so a page of the previous one never shows.
   */
  readonly correctionsPager = createServerPager(
    async (page): Promise<PageResult<OpeningPartyCorrection, { partyId: string }>> => {
      const id = page.filters.id!;
      const result = page.filters.kind === 'supplier'
        ? await this.pages.supplierOpeningCorrectionsPage(id, page.skip, page.take)
        : await this.pages.customerOpeningCorrectionsPage(id, page.skip, page.take);
      return { ...result, summary: { partyId: id } };
    },
    {
      key: 'openingBalance.corrections',
      filters: () => ({ id: this.party()?.id ?? null, kind: this.kind() }),
      enabled: () => {
        const p = this.party();
        return !!p && isOpeningSet(p);
      },
      debounceMs: 0,
    },
  );
  /** The table shows once the party on screen has corrections; a failed read shows the balance alone. */
  readonly correctionsShown = computed(() => {
    const p = this.party();
    return !!p && isOpeningSet(p) && this.correctionsPager.summary()?.partyId === p.id && this.correctionsPager.total() > 0;
  });
  readonly correcting = signal(false);
  readonly action = signal<OpeningDraft>(emptyOpeningDraft(this.today));
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** Shown only after a try, so an empty form does not open on a red line. */
  private readonly tried = signal(false);

  constructor() {
    // Another party starts afresh; its corrections load themselves through their pager.
    effect(() => {
      this.party();
      untracked(() => {
        this.correcting.set(false);
        this.tried.set(false);
        this.error.set(null);
        this.action.set(emptyOpeningDraft(this.today));
        this.reason.set('');
      });
    });
  }

  readonly isSet = isOpeningSet;

  directionOf(signed: number): OpeningDirection {
    return openingDirectionOf(signed, this.kind());
  }

  mayAct(): boolean {
    const user = this.auth.currentUser();
    return this.kind() === 'supplier'
      ? canSetSupplierOpeningBalance(user)
      : canSetOpeningBalance(user);
  }

  k(name: string): string {
    const allowed = ['lead', 'owesUs', 'weOwe', 'setLater', 'adminOnly', 'correction.owesUs', 'correction.weOwe'];
    return this.kind() === 'supplier' && allowed.includes(name)
      ? `sup.opening.${name}`
      : `cust.opening.${name}`;
  }

  draftProblem(): string | null {
    return openingDraftProblem(this.draft(), this.today);
  }

  actionProblem(): string | null {
    return this.tried() ? openingDraftProblem(this.action(), this.today, true) : null;
  }

  reasonMissing(): boolean {
    return this.tried() && !this.reason().trim();
  }

  tone(state: 'set' | 'notSet') {
    return toneOf('openingBalance', state);
  }

  abs(value: number): number {
    return Math.abs(value);
  }

  day(value: string | null | undefined): string {
    // A calendar day, not an instant: read in UTC so no zone moves it to the day before.
    return formatDay(value ? `${value.slice(0, 10)}T00:00:00Z` : null, this.i18n.lang(), 'UTC');
  }

  patchDraft(patch: Partial<OpeningDraft>): void {
    this.draft.update((d) => ({ ...d, ...normalise(patch) }));
  }

  patchAction(patch: Partial<OpeningDraft>): void {
    this.action.update((d) => ({ ...d, ...normalise(patch) }));
  }

  startCorrection(): void {
    this.tried.set(false);
    this.action.set(emptyOpeningDraft(this.today));
    this.reason.set('');
    this.correcting.set(true);
  }

  async setNow(p: OpeningParty): Promise<void> {
    this.tried.set(true);
    const payload = openingPayload(this.action());
    if (openingDraftProblem(this.action(), this.today, true) || !payload) return;
    // Once only: the clerk hears it before the figure becomes fixed.
    const ok = await this.confirm.ask({
      title: this.i18n.t('cust.opening.confirm.title'),
      message: this.i18n.t('cust.opening.confirm.message'),
      confirmLabel: this.i18n.t('cust.opening.set'),
    });
    if (!ok) return;
    await this.run(async () => {
      const updated = this.kind() === 'supplier'
        ? await this.suppliersApi.setOpeningBalance(p.id, payload)
        : await this.admin.setOpeningBalance(p.id, payload);
      this.changed.emit(updated);
    });
  }

  async saveCorrection(p: OpeningParty): Promise<void> {
    this.tried.set(true);
    const payload = openingPayload(this.action());
    if (openingDraftProblem(this.action(), this.today, true) || !payload || !this.reason().trim()) return;

    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('cust.opening.correction.save'))))) return;

    await this.run(async () => {
      const req = { ...payload, reason: this.reason().trim() };
      if (this.kind() === 'supplier') {
        await this.suppliersApi.correctOpeningBalance(p.id, req);
      } else {
        await this.admin.correctOpeningBalance(p.id, req);
      }
      this.correcting.set(false);
      await this.correctionsPager.reload();
      this.changed.emit(null);
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/** A cleared number input gives '' or null; both mean "no amount". */
function normalise(patch: Partial<OpeningDraft>): Partial<OpeningDraft> {
  if (!('amount' in patch)) return patch;
  const amount = patch.amount as unknown;
  return { ...patch, amount: amount === '' || amount === null || amount === undefined ? null : Number(amount) };
}
