import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { localizedName } from '../../../core/multilingual';
import { SearchableProduct } from '../../../core/product-search';
import { ProductPickerComponent } from '../../../shared/product-picker.component';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import {
  MaintenanceApi,
  MaintenanceJob,
  MaintenanceJobWork,
  MaintenancePartBalance,
  MaintenancePartOption,
  MaintenanceVoucherOption,
  jobRequestId,
} from './maintenance-api.service';
import { maintenanceError } from './maintenance-words';

/**
 * The job's Parts and Outside work cards (Phase M2, docs/maintenance/plan.md §2.3, §2.4 and §7).
 *
 * Parts: one compact card per product — issued, returned and still at the job, valued at the frozen costs — with its
 * issue and return documents; a card folds to its summary once nothing of it is at the job or the job is closed. On an
 * open job a `maintenance.jobs.manage` holder issues from the branch's stock (on hand less what production claims there)
 * and returns what was not used, at the cost it went out at. Nothing posts at issue: parts reach the books through the
 * practice ledger (MS-32), said in words.
 *
 * Outside work: what was paid outside, with the expense voucher it points at; a voucher offers only what is not yet
 * linked to production orders' and jobs' expenses. Recorded on an open or completed job.
 */
@Component({
  selector: 'app-maintenance-job-parts',
  standalone: true,
  imports: [FormsModule, ProductPickerComponent, TableStateComponent],
  template: `
    @if (error()) {
      <p class="notice danger" role="alert">{{ error() }}</p>
    }
    @if (done()) {
      <p class="notice ok" role="status">{{ done() }}</p>
    }

    <section class="ui-surface" aria-labelledby="mjp-parts-title">
      <div class="ui-surface__head">
        <h2 id="mjp-parts-title">{{ i18n.t('maint.parts.title') }}</h2>
        @if (work(); as w) {
          <span class="ui-sub">
            {{ i18n.t('maint.parts.summary', { n: qty(w.partsAtJobQty), v: i18n.money(w.partsValue) }) }}
          </span>
        }
      </div>
      <div class="ui-surface__body mjp-body">
        @if (!work() && loading()) {
          <app-table-state kind="loading" />
        } @else if (work(); as w) {
          <p class="bo-hint">{{ i18n.t('maint.parts.postingNote') }}</p>

          @if (w.parts.length === 0) {
            <p class="ui-sub">{{ i18n.t('maint.parts.none') }}</p>
          }
          <div class="mjp-lines">
            @for (p of w.parts; track p.productId) {
              <article class="mjp-line" [class.is-done]="p.atJobQty === 0">
                <header class="mjp-line__head">
                  <div class="mjp-line__title">
                    <span class="mono" dir="ltr">{{ p.sku }}</span>
                    <strong>{{ partName(p) }}</strong>
                  </div>
                  <div class="mjp-line__state">
                    <span class="mono">{{ i18n.money(p.value) }}</span>
                    <span class="ui-sub">{{ lineState(p) }}</span>
                    <button type="button" class="btn-ghost" [attr.aria-expanded]="!folded(p)" (click)="toggle(p)">
                      {{ i18n.t(folded(p) ? 'maint.parts.show' : 'maint.parts.hide') }}
                    </button>
                  </div>
                </header>
                <p class="mjp-line__figures">
                  {{ i18n.t('maint.parts.figures', { issued: qty(p.issuedQty), returned: qty(p.returnedQty), at: qty(p.atJobQty), unit: i18n.money(p.issueUnitCost) }) }}
                </p>
                @if (!folded(p)) {
                  <ul class="mjp-docs">
                    @for (m of movementsOf(p); track m.documentId + m.productId) {
                      <li>
                        <span class="mono" dir="ltr">{{ m.documentNumber }}</span>
                        {{ i18n.t('maint.parts.kind.' + m.kind) }}
                        <span class="mono" dir="ltr">{{ qty(m.qty) }}</span> × <span class="mono">{{ i18n.money(m.unitCost) }}</span>
                        @if (m.costBasis !== 'CostPrice') {
                          <span class="ui-sub">{{ i18n.t('maint.parts.basis.' + m.costBasis) }}</span>
                        }
                        <span class="ui-sub">{{ i18n.t('maint.job.byOn', { who: m.userName ?? '—', date: i18n.dateTime(m.atUtc) }) }}</span>
                      </li>
                    }
                  </ul>
                  @if (open() && canManage() && p.atJobQty > 0) {
                    <div class="mjp-return">
                      <label [attr.for]="'mjp-ret-' + p.productId">{{ i18n.t('maint.parts.returnQty') }}</label>
                      <input
                        [attr.id]="'mjp-ret-' + p.productId"
                        type="number"
                        min="0"
                        step="any"
                        class="num mono"
                        [ngModel]="returning[p.productId] ?? null"
                        (ngModelChange)="returning[p.productId] = $event"
                      />
                      <button type="button" class="btn-ghost" [disabled]="busy() || !returnable(p)" (click)="giveBack(p)">
                        {{ i18n.t('maint.parts.return') }}
                      </button>
                      <button type="button" class="btn-ghost" [disabled]="busy()" (click)="returning[p.productId] = p.atJobQty">
                        {{ i18n.t('maint.parts.returnAll', { n: qty(p.atJobQty) }) }}
                      </button>
                    </div>
                  }
                }
              </article>
            }
          </div>

          @if (open() && canManage()) {
            <div class="mjp-issue bo-form" role="group" aria-labelledby="mjp-issue-title">
              <h3 id="mjp-issue-title">{{ i18n.t('maint.parts.issueHeading') }}</h3>
              <div class="bo-field">
                <label for="mjp-product">{{ i18n.t('maint.parts.product') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <app-product-picker
                  inputId="mjp-product"
                  [products]="pickable()"
                  [productId]="issue.productId"
                  [note]="availableNote"
                  (picked)="issue.productId = $event.id"
                />
                @if (chosen(); as c) {
                  <p class="bo-hint">{{ i18n.t('maint.parts.available', { n: qty(c.available) }) }}</p>
                } @else if (options() && options()!.products.length === 0) {
                  <p class="bo-hint">{{ i18n.t('maint.parts.nothingToIssue') }}</p>
                }
              </div>
              <div class="bo-field">
                <label for="mjp-qty">{{ i18n.t('maint.parts.qty') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="mjp-qty" type="number" min="0" step="any" class="num mono" [(ngModel)]="issue.qty" />
              </div>
              <div>
                <button type="button" class="btn-primary" [disabled]="busy() || !issuable()" (click)="give()">{{ i18n.t('maint.parts.issue') }}</button>
              </div>
            </div>
          }
        }
      </div>
    </section>

    <section class="ui-surface" aria-labelledby="mjp-work-title">
      <div class="ui-surface__head">
        <h2 id="mjp-work-title">{{ i18n.t('maint.outside.title') }}</h2>
        @if (work(); as w) {
          <span class="ui-sub">{{ i18n.t('maint.outside.total', { v: i18n.money(w.expensesTotal) }) }}</span>
        }
      </div>
      <div class="ui-surface__body mjp-body">
        @if (work(); as w) {
          <p class="bo-hint">{{ i18n.t('maint.outside.note') }}</p>
          @if (w.expenses.length === 0) {
            <p class="ui-sub">{{ i18n.t('maint.outside.none') }}</p>
          } @else {
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('maint.outside.description') }}</th>
                  <th class="num">{{ i18n.t('maint.outside.amount') }}</th>
                  <th>{{ i18n.t('maint.outside.voucher') }}</th>
                  <th>{{ i18n.t('maint.outside.recorded') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (e of w.expenses; track e.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('maint.outside.description')">{{ e.description }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.outside.amount')">{{ i18n.money(e.amount) }}</td>
                    <td [attr.data-label]="i18n.t('maint.outside.voucher')">
                      @if (e.voucherNumber) {
                        <span class="mono" dir="ltr">{{ e.voucherNumber }}</span>
                        <span class="ui-sub">{{ i18n.t('maint.voucherStatus.' + e.voucherStatus) }}</span>
                      } @else {
                        <span class="ui-sub">{{ i18n.t('maint.outside.noVoucher') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('maint.outside.recorded')">
                      {{ i18n.t('maint.job.byOn', { who: e.userName ?? '—', date: i18n.dateTime(e.atUtc) }) }}
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          }

          @if (recordable() && canManage()) {
            <div class="bo-form" role="group" aria-labelledby="mjp-exp-title">
              <h3 id="mjp-exp-title">{{ i18n.t('maint.outside.add') }}</h3>
              <div class="bo-field">
                <label for="mjp-exp-desc">{{ i18n.t('maint.outside.description') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="mjp-exp-desc" maxlength="300" [(ngModel)]="expense.description" />
              </div>
              <div class="bo-field">
                <label for="mjp-exp-amount">{{ i18n.t('maint.outside.amount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="mjp-exp-amount" type="number" min="0" step="any" class="num mono" [(ngModel)]="expense.amount" />
              </div>
              <div class="bo-field">
                <label for="mjp-exp-voucher">{{ i18n.t('maint.outside.voucher') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                <select id="mjp-exp-voucher" [(ngModel)]="expense.voucherId">
                  <option [ngValue]="null">{{ i18n.t('maint.outside.noVoucher') }}</option>
                  @for (v of options()?.vouchers ?? []; track v.id) {
                    <option [ngValue]="v.id">{{ voucherLabel(v) }}</option>
                  }
                </select>
                <p class="bo-hint">{{ i18n.t('maint.outside.voucherHint') }}</p>
                @if (voucherShort()) {
                  <p class="notice warn" role="status">{{ i18n.t('maint.outside.voucherShort', { v: i18n.money(chosenVoucher()!.free) }) }}</p>
                }
              </div>
              <div>
                <button type="button" class="btn-primary" [disabled]="busy() || !expenseReady()" (click)="record()">{{ i18n.t('maint.outside.record') }}</button>
              </div>
            </div>
          }
        }
      </div>
    </section>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      :host {
        display: contents;
      }
      .mjp-body {
        display: grid;
        gap: var(--sp-3);
      }
      .mjp-lines {
        display: grid;
        gap: var(--sp-2);
      }
      .mjp-line {
        display: grid;
        gap: var(--sp-1);
        padding: var(--sp-2) var(--sp-3);
        background: var(--surface);
        border: var(--line) solid var(--border);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow-sm);
      }
      .mjp-line.is-done {
        background: var(--surface-2);
        box-shadow: none;
      }
      .mjp-line__head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-2);
      }
      .mjp-line__title,
      .mjp-line__state {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: var(--sp-2);
      }
      .mjp-line__figures {
        margin: 0;
        color: var(--muted);
        font-size: var(--fs-sm, 0.9em);
      }
      .mjp-docs {
        display: grid;
        gap: var(--sp-1);
        margin: 0;
        padding-inline-start: var(--sp-4);
      }
      .mjp-return {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
      }
      .mjp-return input {
        inline-size: 8rem;
      }
      .mjp-issue h3,
      .bo-form h3 {
        margin: 0;
        font-size: var(--fs-md, 1rem);
      }
    `,
  ],
})
export class MaintenanceJobPartsComponent {
  private readonly api = inject(MaintenanceApi);
  private readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);

  readonly job = input.required<MaintenanceJob>();
  /** The job's parts and outside work each time they are read or changed: the job page says what is still at the job. */
  readonly changed = output<MaintenanceJobWork>();

  readonly work = signal<MaintenanceJobWork | null>(null);
  readonly options = signal<{ products: MaintenancePartOption[]; vouchers: MaintenanceVoucherOption[] } | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  private readonly unfolded = signal<ReadonlySet<string>>(new Set());
  private readonly refolded = signal<ReadonlySet<string>>(new Set());

  issue: { productId: string | null; qty: number | null } = { productId: null, qty: null };
  returning: Record<string, number | null> = {};
  expense: { description: string; amount: number | null; voucherId: string | null } = { description: '', amount: null, voucherId: null };
  /** One id per attempt: a retried click answers the first document instead of moving the parts twice. */
  private pending: { key: string; id: string } | null = null;

  readonly canManage = computed(() => mayDo(this.auth.currentUser(), 'maintenance.jobs.manage'));
  readonly open = computed(() => this.job().status === 'Open');
  /** Outside work is recorded on an open job and on a completed one (an invoice that arrives after the repair). */
  readonly recordable = computed(() => this.job().status === 'Open' || this.job().status === 'Completed');
  readonly pickable = computed<SearchableProduct[]>(() => this.options()?.products ?? []);
  readonly availableNote = (p: SearchableProduct): string | null => {
    const option = this.options()?.products.find((o) => o.id === p.id);
    return option ? this.i18n.t('maint.parts.available', { n: this.qty(option.available) }) : null;
  };

  constructor() {
    // Read again whenever the job changes (taken, completed, cancelled): its parts and what may be done with them.
    effect(() => {
      const j = this.job();
      untracked(() => void this.load(j));
    });
  }

  chosen(): MaintenancePartOption | null {
    const id = this.issue.productId;
    return id ? (this.options()?.products.find((p) => p.id === id) ?? null) : null;
  }

  chosenVoucher(): MaintenanceVoucherOption | null {
    const id = this.expense.voucherId;
    return id ? (this.options()?.vouchers.find((v) => v.id === id) ?? null) : null;
  }

  qty(value: number | null | undefined): string {
    return formatQty(value ?? 0, this.i18n.lang());
  }

  partName(p: MaintenancePartBalance): string {
    return localizedName({ nameAr: p.nameAr, nameFr: p.nameFr, nameEn: p.nameEn }, this.i18n.lang()) || p.sku;
  }

  /** Where the product stands, in words: at the job, returned in full, or used by the repair once the job is closed. */
  lineState(p: MaintenancePartBalance): string {
    if (p.atJobQty === 0) return this.i18n.t('maint.parts.state.returned');
    return this.open() ? this.i18n.t('maint.parts.state.atJob') : this.i18n.t('maint.parts.state.used');
  }

  /** Folded to its summary once nothing of it is at the job, or the job is closed; the reader may open it. */
  folded(p: MaintenancePartBalance): boolean {
    const done = p.atJobQty === 0 || !this.open();
    return done ? !this.unfolded().has(p.productId) : this.refolded().has(p.productId);
  }

  toggle(p: MaintenancePartBalance): void {
    const flip = (set: ReadonlySet<string>) => {
      const next = new Set(set);
      if (!next.delete(p.productId)) next.add(p.productId);
      return next;
    };
    const done = p.atJobQty === 0 || !this.open();
    if (done) this.unfolded.update(flip);
    else this.refolded.update(flip);
  }

  movementsOf(p: MaintenancePartBalance) {
    return (this.work()?.movements ?? []).filter((m) => m.productId === p.productId);
  }

  issuable(): boolean {
    const c = this.chosen();
    const q = Number(this.issue.qty);
    return !!c && Number.isFinite(q) && q > 0 && q <= c.available;
  }

  returnable(p: MaintenancePartBalance): boolean {
    const q = Number(this.returning[p.productId]);
    return Number.isFinite(q) && q > 0 && q <= p.atJobQty;
  }

  voucherLabel(v: MaintenanceVoucherOption): string {
    const who = v.payeeName || v.purpose || '';
    return `${v.number}${who ? ' — ' + who : ''} — ${this.i18n.t('maint.outside.free', { v: this.i18n.money(v.free) })}`;
  }

  voucherShort(): boolean {
    const v = this.chosenVoucher();
    const amount = Number(this.expense.amount);
    return !!v && Number.isFinite(amount) && amount > v.free;
  }

  expenseReady(): boolean {
    const amount = Number(this.expense.amount);
    return this.expense.description.trim().length > 0 && Number.isFinite(amount) && amount > 0 && !this.voucherShort();
  }

  async give(): Promise<void> {
    const c = this.chosen();
    if (!c || !this.issuable()) return;
    const qty = Number(this.issue.qty);
    await this.act(
      (id) => this.api.moveParts(this.job().id, 'Issue', [{ productId: c.id, qty }], id),
      `issue|${c.id}|${qty}`,
      'maint.parts.issued',
      () => (this.issue = { productId: null, qty: null }),
    );
  }

  async giveBack(p: MaintenancePartBalance): Promise<void> {
    if (!this.returnable(p)) return;
    const qty = Number(this.returning[p.productId]);
    await this.act(
      (id) => this.api.moveParts(this.job().id, 'Return', [{ productId: p.productId, qty }], id),
      `return|${p.productId}|${qty}`,
      'maint.parts.returned',
      () => delete this.returning[p.productId],
    );
  }

  async record(): Promise<void> {
    if (!this.expenseReady()) return;
    const { description, amount, voucherId } = this.expense;
    await this.act(
      (id) => this.api.addExpense(this.job().id, description.trim(), Number(amount), voucherId, id),
      `expense|${description.trim()}|${amount}|${voucherId}`,
      'maint.outside.recordedNotice',
      () => (this.expense = { description: '', amount: null, voucherId: null }),
    );
  }

  private async load(job: MaintenanceJob): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const work = await this.api.work(job.id);
      this.show(work);
      const live = job.status === 'Open' || job.status === 'Completed';
      this.options.set(live && this.canManage() ? await this.api.workOptions(job.id) : null);
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.loading.set(false);
    }
  }

  private show(work: MaintenanceJobWork): void {
    this.work.set(work);
    this.changed.emit(work);
  }

  /** The same request (same key) keeps its id until it succeeds, so a retry after a lost answer moves nothing twice. */
  private async act(call: (requestId: string) => Promise<MaintenanceJobWork>, key: string, notice: string, reset: () => void): Promise<void> {
    if (this.pending?.key !== key) this.pending = { key, id: jobRequestId() };
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      this.show(await call(this.pending.id));
      this.pending = null;
      reset();
      this.done.set(this.i18n.t(notice));
      if (this.canManage()) this.options.set(await this.api.workOptions(this.job().id));
    } catch (e) {
      this.error.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
