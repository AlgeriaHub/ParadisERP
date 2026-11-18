import type { PaymentVoucherBonusLine } from './payment-vouchers-api.service';
import { Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminPaymentMethod, adminErrorMessage } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { PrintService } from '../../print/print.service';
import { formatDay, localizedName } from '../backoffice-format';
import { withZ } from './receivable-view';
import { ConfirmService } from '../../../ui/confirm.service';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { PageChrome } from '../shell/page-chrome';
import { STATUS_NAME, kindKey, statusKey, voucherActions, voucherReader, warningText } from './payment-voucher-view';
import { PaymentVoucher, PaymentVoucherResponse, PaymentVouchersApi, REFUND } from './payment-vouchers-api.service';

type Act = 'approve' | 'pay' | 'cancel';

/**
 * One payment voucher: what it is for, where it stands in words, and the one act its state
 * allows.
 *
 * <p>Pending: a manager other than the requester — or the administrator — approves it; nothing
 * has moved. Approved: somebody pays it, which is the moment the cash leaves the branch's box and
 * the books move, exactly once. Either may be cancelled, with a reason. Paid: the screen offers
 * nothing but the paper — a paid voucher is not cancelled, the money has left.</p>
 */
@Component({
  selector: 'app-payment-voucher-detail',
  standalone: true,
  imports: [FormsModule, StatusComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (done(); as text) {
        <p class="notice ok" role="status">{{ text }}</p>
      }
      @if (warning(); as text) {
        <p class="notice warn" role="status">{{ text }}</p>
      }

      @if (notFound()) {
        <p class="notice danger" role="alert">{{ i18n.t('pv.notFound') }}</p>
      } @else {
        @if (voucher(); as v) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title pv-title">
              <span class="mono">{{ v.number }}</span>
              <span class="bo-soft">{{ i18n.t(kindKey(v.kind)) }}</span>
            </h2>
            <button type="button" class="btn-ghost bo-small" (click)="print(v)">{{ i18n.t('common.print') }}</button>
            <p class="bo-meta bo-panel__note">
              <app-status [tone]="tone(v)">{{ i18n.t(statusKey(v.status)) }}</app-status>
              <span>{{ i18n.t('pv.view.createdOn') }} <span class="mono">{{ day(v.createdAtUtc) }}</span></span>
              <span>{{ i18n.t('rent.branch') }} {{ branchName(v) }}</span>
              @if (v.requestedBy) {
                <span>{{ i18n.t('pv.view.requestedBy') }} {{ v.requestedBy }}</span>
              }
            </p>
          </div>

          <div class="bo-panel__body">
            <p class="notice" [class.warn]="v.status === 1">{{ i18n.t('pv.status.' + stateName(v)) }}</p>
          </div>

          <div class="bo-panel__body">
            <div class="bo-metrics">
              <div class="bo-metric bo-metric--lead">
                <span class="bo-metric__label">{{ i18n.t('pay.amount') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(v.amount) }}</strong>
                <span class="bo-metric__note"><span>{{ methodName(v.methodKey, v.methodName) }}</span></span>
              </div>
              @if (v.deductedFromDebt > 0) {
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('pv.view.deducted') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(v.deductedFromDebt) }}</strong>
                  <span class="bo-metric__note"><span>{{ i18n.t('pv.view.deductedNote') }}</span></span>
                </div>
              }
              @if (v.returnNoteUnusedRent !== null && v.returnNoteUnusedRent > 0) {
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('pv.view.unusedRent') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(v.returnNoteUnusedRent) }}</strong>
                  @if (v.returnNoteDebtReduction) {
                    <span class="bo-metric__note">
                      <span>{{ i18n.t('pv.view.returnDeduction', { v: i18n.money(v.returnNoteDebtReduction) }) }}</span>
                    </span>
                  }
                </div>
              }
            </div>
          </div>

          <div class="bo-panel__body">
            <dl class="pv-facts">
              <dt>{{ i18n.t('pv.view.payee') }}</dt>
              <dd>{{ v.payeeName || v.customerName || '—' }}</dd>
              @if (v.kind === REFUND) {
                <dt>{{ i18n.t('common.customer') }}</dt>
                <dd>{{ v.customerName || '—' }}</dd>
                @if (v.contractNumber) {
                  <dt>{{ i18n.t('pv.view.contract') }}</dt>
                  <dd class="mono">{{ v.contractNumber }}</dd>
                }
                @if (v.returnNoteNumber) {
                  <dt>{{ i18n.t('rent.returnNote') }}</dt>
                  <dd class="mono">{{ v.returnNoteNumber }}</dd>
                }
                @if (v.unusedDays) {
                  <dt>{{ i18n.t('pv.view.unusedDays') }}</dt>
                  <dd class="mono">{{ v.unusedDays }}</dd>
                }
              } @else if (v.expenseCategory) {
                <dt>{{ i18n.t('pv.form.category') }}</dt>
                <dd>{{ categoryWord(v.expenseCategory) }} <span class="bo-soft mono">{{ v.expenseAccountCode }}</span></dd>
              }
              @if (v.instrumentNumber) {
                <dt>{{ i18n.t('inv.rcpt.instrumentNumber') }}</dt>
                <dd class="mono">{{ v.instrumentNumber }}</dd>
              }
              @if (v.instrumentBank) {
                <dt>{{ i18n.t('pay.instrumentBank') }}</dt>
                <dd>{{ v.instrumentBank }}</dd>
              }
              <dt>{{ i18n.t('pv.form.purpose') }}</dt>
              <dd>{{ v.purpose || '—' }}</dd>
              @if (v.bonusLines?.length) {
                <!-- Workers' bonuses (owner, 2026-11-08): who received what, and the payroll that carries it. -->
                <dt>{{ i18n.t('pv.category.workersBonus') }}</dt>
                <dd>
                  <table class="ui-table">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('pv.bonus.worker') }}</th>
                        <th class="num">{{ i18n.t('pv.bonus.amount') }}</th>
                        <th>{{ i18n.t('pv.bonus.payslip') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (b of v.bonusLines; track b.employeeId) {
                        <tr>
                          <td><bdi>{{ b.employeeName }}</bdi> <span class="ui-sub mono" dir="ltr">{{ b.employeeNumber }}</span></td>
                          <td class="num mono">{{ i18n.money(b.amount) }}</td>
                          <td>{{ bonusState(b) }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </dd>
              }
              @if (v.notes) {
                <dt>{{ i18n.t('rent.notes') }}</dt>
                <dd>{{ v.notes }}</dd>
              }
              @if (v.approvedAtUtc) {
                <dt>{{ i18n.t('pv.view.approvedBy') }}</dt>
                <dd><bdi>{{ v.approvedByName || '—' }}</bdi>&ngsp;<bdi class="mono">{{ when(v.approvedAtUtc) }}</bdi></dd>
              }
              @if (v.paidAtUtc) {
                <dt>{{ i18n.t('pv.view.paidBy') }}</dt>
                <dd><bdi>{{ v.paidByName || '—' }}</bdi>&ngsp;<bdi class="mono">{{ when(v.paidAtUtc) }}</bdi></dd>
              }
              @if (v.cancelledAtUtc) {
                <dt>{{ i18n.t('pv.view.cancelledOn') }}</dt>
                <dd class="mono">{{ when(v.cancelledAtUtc) }}</dd>
              }
              @if (v.statusNote) {
                <dt>{{ i18n.t('pv.view.statusNote') }}</dt>
                <dd>{{ v.statusNote }}</dd>
              }
            </dl>
          </div>
        </section>

        @if (actions(); as a) {
          @if (a.approve || a.approveIsOwn || a.pay || a.cancel) {
            <section class="bo-panel">
              <div class="bo-panel__head">
                <h2 class="bo-panel__title">{{ i18n.t('pv.act.title') }}</h2>
              </div>
              <div class="bo-panel__body bo-form">
                @if (a.approveIsOwn) {
                  <p class="notice">{{ i18n.t('pv.act.ownVoucher') }}</p>
                }
                @if (a.pay) {
                  <p class="notice warn">{{ i18n.t('pv.act.payNote') }}</p>
                }
                <div class="bo-fields">
                  <div class="bo-field bo-field--wide">
                    <label for="pv-note">
                      {{ i18n.t('pv.act.note') }}<span class="bo-req">{{ i18n.t('pv.act.noteRule') }}</span>
                    </label>
                    <input id="pv-note" maxlength="300" [ngModel]="note()" (ngModelChange)="note.set($event)" />
                    @if (needsNote()) {
                      <span class="bo-error" role="alert">{{ i18n.t('pv.act.cancelNeedsNote') }}</span>
                    }
                  </div>
                </div>
              </div>
              <div class="bo-panel__foot">
                @if (a.cancel) {
                  <button type="button" class="btn-danger" [disabled]="busy()" (click)="act('cancel', v)">{{ i18n.t('pv.act.cancel') }}</button>
                }
                @if (a.approve) {
                  <button type="button" class="btn-primary" [disabled]="busy()" (click)="act('approve', v)">{{ i18n.t('pv.act.approve') }}</button>
                }
                @if (a.pay) {
                  <button type="button" class="btn-primary" [disabled]="busy()" (click)="act('pay', v)">{{ i18n.t('pv.act.pay') }}</button>
                }
              </div>
            </section>
          }
        }
        } @else {
          <p class="bo-loading">
            <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      .pv-title {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 0.5rem;
      }
      .pv-facts {
        display: grid;
        grid-template-columns: minmax(8rem, max-content) 1fr;
        gap: 0.35rem 1rem;
        margin: 0;
      }
      .pv-facts dt {
        color: var(--muted);
        font-size: 0.86rem;
      }
      .pv-facts dd {
        margin: 0;
      }
      @media (max-width: 40rem) {
        .pv-facts {
          grid-template-columns: 1fr;
        }
        .pv-facts dd {
          margin-block-end: 0.35rem;
        }
      }
    `,
  ],
})
export class PaymentVoucherDetailComponent implements OnInit {
  private readonly api = inject(PaymentVouchersApi);
  private readonly auth = inject(AuthService);
  private readonly printer = inject(PrintService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  private readonly regional = inject(RegionalService);

  readonly id = input.required<string>();
  readonly methods = input<AdminPaymentMethod[]>([]);
  readonly branches = input<AdminBranch[]>([]);
  /** The response of the act that brought the reader here — a create, usually — with its warning. */
  readonly arrival = input<PaymentVoucherResponse | null>(null);
  private readonly chrome = inject(PageChrome, { optional: true });

  constructor() {
    // The voucher names the page; the breadcrumb's "Payment vouchers" is the way back to the list.
    effect(() => {
      const v = this.voucher();
      const missing = this.notFound() || !!this.error();
      untracked(() => {
        this.chrome?.heading.set(v ? v.number : missing ? '—' : this.i18n.t('common.loading'));
        this.chrome?.description.set(v ? this.i18n.t(kindKey(v.kind)) : null);
        this.chrome?.trail.set([]);
      });
    });
  }

  /** The branch in the screen's language, as the list names it; the server's name is French. */
  branchName(v: PaymentVoucher): string {
    const branch = this.branches().find((b) => b.id === v.branchId);
    return branch ? localizedName(branch, this.i18n.lang()) : v.branchName;
  }

  readonly REFUND = REFUND;
  readonly kindKey = kindKey;
  readonly statusKey = statusKey;

  readonly voucher = signal<PaymentVoucher | null>(null);
  readonly notFound = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly warning = signal<string | null>(null);
  readonly busy = signal(false);
  readonly note = signal('');
  readonly needsNote = signal(false);

  readonly actions = computed(() => {
    const v = this.voucher();
    return v ? voucherActions(v, voucherReader(this.auth.currentUser())) : null;
  });

  async ngOnInit(): Promise<void> {
    const arrival = this.arrival();
    if (arrival) {
      this.voucher.set(arrival.voucher);
      this.done.set(this.i18n.t('pv.form.saved', { n: arrival.voucher.number }));
      this.warning.set(this.warningOf(arrival));
    }
    try {
      this.voucher.set(await this.api.get(this.id()));
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) this.notFound.set(true);
      else this.error.set(adminErrorMessage(e));
    }
  }

  stateName(v: PaymentVoucher): string {
    return STATUS_NAME[v.status] ?? String(v.status);
  }

  tone(v: PaymentVoucher) {
    return toneOf('voucher', STATUS_NAME[v.status]);
  }

  categoryWord(category: string | null): string {
    if (!category) return '—';
    const key = 'pv.category.' + category;
    const word = this.i18n.t(key);
    return word === key ? category : word;
  }

  methodName(key: string, fallback: string): string {
    const method = this.methods().find((m) => m.key === key);
    return method ? localizedName(method, this.i18n.lang()) : fallback;
  }

  day(value: string | null): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }

  when(value: string | null): string {
    return value ? this.i18n.dateTime(withZ(value)) : '—';
  }

  print(v: PaymentVoucher): void {
    this.printer.open('payment-voucher', v.id);
  }

  async act(kind: Act, v: PaymentVoucher): Promise<void> {
    const note = this.note().trim();
    this.needsNote.set(kind === 'cancel' && !note);
    if (this.needsNote()) return;
    const words = {
      approve: { title: 'pv.act.approveTitle', message: 'pv.act.approveConfirm', ok: 'pv.act.approve' },
      pay: { title: 'pv.act.payTitle', message: 'pv.act.payConfirm', ok: 'pv.act.pay' },
      cancel: { title: 'pv.act.cancelTitle', message: 'pv.act.cancelConfirm', ok: 'pv.act.cancel' },
    }[kind];
    const params = { n: v.number, v: this.i18n.money(v.amount) };
    const yes = await this.confirm.ask({
      title: this.i18n.t(words.title, params),
      message: this.i18n.t(words.message, params),
      confirmLabel: this.i18n.t(words.ok),
      // "Keep" rather than "Cancel" beside "Cancel the voucher", or the two buttons say the same.
      cancelLabel: kind === 'cancel' ? this.i18n.t('confirm.keep') : undefined,
      // Cancelling ends the voucher; paying and approving move it on as intended.
      danger: kind === 'cancel',
    });
    if (!yes) return;

    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    this.warning.set(null);
    try {
      const response =
        kind === 'approve' ? await this.api.approve(v.id, note || null)
        : kind === 'pay' ? await this.api.pay(v.id, note || null)
        : await this.api.cancel(v.id, note);
      this.voucher.set(response.voucher);
      this.note.set('');
      this.done.set(this.i18n.t('pv.act.done', { n: response.voucher.number, state: this.i18n.t(statusKey(response.voucher.status)) }));
      this.warning.set(this.warningOf(response));
    } catch (e) {
      // The usual refusal is a state that moved elsewhere; the re-read below shows the truth.
      this.error.set(adminErrorMessage(e));
      try {
        this.voucher.set(await this.api.get(v.id));
      } catch {
        /* the error above already says what went wrong */
      }
    } finally {
      this.busy.set(false);
    }
  }

  private warningOf(response: PaymentVoucherResponse): string | null {
    return warningText(response, (k, p) => this.i18n.t(k, p), (n) => this.i18n.money(n));
  }

  /** Where a worker's bonus stands on payroll, in words. */
  bonusState(b: PaymentVoucherBonusLine): string {
    if (b.runNumber) return this.i18n.t(b.settled ? 'pv.bonus.settled' : 'pv.bonus.onRun', { run: b.runNumber });
    return this.i18n.t('pv.bonus.waiting', { month: `${String(b.payrollMonth).padStart(2, '0')}/${b.payrollYear}` });
  }
}
