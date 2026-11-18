import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { adminErrorMessage, problemCode } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService, localized } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { DialogComponent } from '../../../ui/dialog.component';
import { ReceiptVoucher, ReceivableSettings, ReceivablesApi } from './receivables-api.service';

/** The permission the keep route asks for: the one that approves refund payment vouchers (SAL-13). */
export const KEEP_ADVANCE_PERMISSION = 'finance.vouchers.approve';

/**
 * What of a receipt may still be kept: an advance's part still on 419, or a payment's unallocated part on 411 (the
 * server also caps that by the customer's credit). 0 for anything else, or for money that has not arrived.
 */
export function keepable(v: ReceiptVoucher): number {
  if (v.status !== 'Confirmed') return 0;
  if (v.kind === 'Advance') return Math.max(0, v.advanceOpen ?? 0);
  if ((v.kind ?? 'Payment') === 'Payment') return Math.max(0, v.unallocated);
  return 0;
}

/** The setting allows keeping (SAL-13: not allowed by default) and names the income account to credit. */
export function keepingAllowed(s: ReceivableSettings | null | undefined): boolean {
  return s?.keptAdvances === 'OtherIncome' && !!s.keptIncomeAccount;
}

/** The refusals the dialog says in the reader's language; anything else falls back to the server's sentence. */
const REFUSALS: Readonly<Record<string, string>> = {
  kept_advances_not_allowed: 'adv.keep.error.notAllowed',
  accounts_not_mapped: 'adv.keep.error.account',
};

/**
 * SAL-13 (owner 2026-11-10, answer A; the accountant's setting): keep all or part of a customer's advance on a cancelled
 * order. Opened from a receipt's ⋯ menu when the setting allows it and the reader may approve refunds. The amount starts
 * at all the receipt still holds; the reason is required. Asked once more before it acts: there is no undo. The server
 * moves the amount from 419 (or 411) to the income account the accountant chose; the rest is refunded the usual way.
 */
@Component({
  selector: 'app-keep-advance-dialog',
  standalone: true,
  imports: [FormsModule, DialogComponent],
  template: `
    <app-dialog [open]="!!receipt()" size="sm" [heading]="i18n.t('adv.keep.heading', { n: receipt()?.number ?? '' })" (dismiss)="dismiss.emit()">
      @if (receipt(); as v) {
        <div class="bo-form">
          @if (error()) {
            <p class="notice danger" role="alert">{{ error() }}</p>
          }
          <p class="bo-hint">
            {{ i18n.t(v.kind === 'Advance' ? 'adv.keep.lead419' : 'adv.keep.lead411', { account: account() }) }}
            @if (v.kind === 'Advance' && (v.advanceTaxAmount ?? 0) > 0) {
              {{ i18n.t('adv.keep.leadTax', { tax: taxName() }) }}
            }
          </p>
          <p class="keep-holds">
            <span>{{ i18n.t(v.kind === 'Advance' ? 'adv.keep.holds419' : 'adv.keep.holds411') }}</span>
            <strong class="mono">{{ i18n.money(max()) }}</strong>
          </p>
          <div class="bo-field" [class.is-invalid]="amountProblem()">
            <label for="keep-amount">{{ i18n.t('pay.amount') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="keep-amount" type="number" min="0" [max]="max()" [step]="regional.moneyStep()" class="num mono"
              [ngModel]="amount()" (ngModelChange)="amount.set($event === '' || $event === null ? null : +$event)"
              [attr.aria-invalid]="amountProblem() ? 'true' : null"
              [attr.aria-describedby]="amountProblem() ? 'keep-amount-error' : null" />
            @if (amountProblem()) {
              <span id="keep-amount-error" class="bo-error" role="alert">{{ i18n.t('adv.keep.amountProblem', { amount: i18n.money(max()) }) }}</span>
            }
          </div>
          <div class="bo-field" [class.is-invalid]="reasonMissing()">
            <label for="keep-reason">{{ i18n.t('adv.keep.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <textarea id="keep-reason" rows="2" maxlength="400" [ngModel]="reason()" (ngModelChange)="reason.set($event)"
              [attr.aria-invalid]="reasonMissing() ? 'true' : null"
              [attr.aria-describedby]="reasonMissing() ? 'keep-reason-error' : 'keep-reason-help'"></textarea>
            @if (reasonMissing()) {
              <span id="keep-reason-error" class="bo-error" role="alert">{{ i18n.t('adv.keep.reasonRequired') }}</span>
            } @else {
              <span id="keep-reason-help" class="bo-hint">{{ i18n.t('adv.keep.reasonHint') }}</span>
            }
          </div>
          <p class="bo-hint">{{ i18n.t('adv.keep.rest') }}</p>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="dismiss.emit()">{{ i18n.t('common.close') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy()" (click)="keep()">{{ i18n.t('adv.keep.action') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .keep-holds {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: var(--sp-2);
        margin: 0;
      }
    `,
  ],
})
export class KeepAdvanceDialogComponent {
  private readonly api = inject(ReceivablesApi);
  private readonly confirm = inject(ConfirmService);
  readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);

  /** The receipt to keep from; null keeps the dialog closed. */
  readonly receipt = input<ReceiptVoucher | null>(null);
  /** The income account the setting credits, as the reader reads it ("7585 — Autres produits"). */
  readonly incomeAccount = input<string | null>(null);
  readonly kept = output<ReceiptVoucher>();
  readonly dismiss = output<void>();

  readonly amount = signal<number | null>(null);
  readonly reason = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** Errors show once the reader has tried to keep, not while they type. */
  readonly tried = signal(false);

  constructor() {
    // Each opening starts afresh: all the receipt still holds, no reason yet.
    effect(() => {
      const v = this.receipt();
      untracked(() => {
        if (!v) return;
        this.amount.set(keepable(v));
        this.reason.set('');
        this.error.set(null);
        this.tried.set(false);
      });
    });
  }

  max(): number {
    const v = this.receipt();
    return v ? keepable(v) : 0;
  }

  account(): string {
    return this.incomeAccount() || '—';
  }

  taxName(): string {
    return localized(this.regional.settings().taxNames, this.i18n.lang());
  }

  amountProblem(): boolean {
    const amount = this.amount();
    return this.tried() && (amount === null || !(amount > 0) || amount > this.max());
  }

  reasonMissing(): boolean {
    return this.tried() && !this.reason().trim();
  }

  async keep(): Promise<void> {
    this.tried.set(true);
    const v = this.receipt();
    const amount = this.amount();
    const reason = this.reason().trim();
    if (!v || this.amountProblem() || this.reasonMissing() || amount === null) return;
    const message = this.i18n.t('adv.keep.confirm', { amount: this.i18n.money(amount), n: v.number, account: this.account(), reason });
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('adv.keep.action'), message)))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      this.kept.emit(await this.api.keepAdvance(v.id, { amount, reason }));
    } catch (e) {
      const key = REFUSALS[problemCode(e) ?? ''];
      this.error.set(key ? this.i18n.t(key) : adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
