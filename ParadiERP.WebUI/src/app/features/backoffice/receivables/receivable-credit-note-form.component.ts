import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminCustomer, adminErrorMessage } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { TouchedFields } from '../backoffice-forms';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { DocRef, INVOICE_TONE, midday, roundTo, stateKey, taxByRate, today } from './receivable-view';
import { DateInputComponent } from '../../../ui/date-input.component';
import { CreateCreditNoteRequest, CreditNote, Invoice, InvoiceLine, ReceivablesApi } from './receivables-api.service';

/** Everything left on every line, or a quantity typed per line. */
type Scope = 'all' | 'some';

/** One invoice line as this screen reads it: what was billed, what is already back, what this note would give back. */
interface CreditRow {
  line: InvoiceLine;
  left: number;
  qtyToCredit: number;
  /** Pro-rata on the line's own net, so net of its discount — the server's arithmetic. */
  credit: number;
  /** P0.3: what this line's invoice took out of stock and has not come back yet; 0 when it took nothing out. */
  out: number;
  /** Goods of the line coming back into stock with this note; at most `backMax`. */
  back: number;
  /** min(qtyToCredit, out): never more back in stock than this note credits, nor than is still out. */
  backMax: number;
}

type CreditProblem = 'reasonRequired' | 'nothingToCredit' | 'creditQtyRange' | 'backToStockRange' | 'ackRequired';

/**
 * What on this invoice am I giving back, and why?
 *
 * <p>A credit note is the only correction an invoice ever gets: nothing is edited, nothing is
 * deleted. That is why the reason is not optional — an unexplained credit note is a debt
 * quietly written off — and why the act has to be acknowledged before it is saved, like the
 * lock on an issue note: it posts to the ledger and reduces the customer account the moment
 * it lands, and there is no un-doing it.</p>
 *
 * <p>Prices, rates and income accounts are inherited from the invoice lines and never
 * re-entered; the screen only asks how much of each line comes back. The preview mirrors the
 * server's arithmetic — pro-rata on each line's own net, tax per rate on the rate's total —
 * and says so, because the definitive figure is the one the server answers with.</p>
 *
 * <p>Crediting more than is still outstanding is allowed (the customer may have paid first);
 * the invoice then reads as owed to the customer. The screen states that consequence rather
 * than preventing it.</p>
 */
@Component({
  selector: 'app-receivable-credit-note-form',
  standalone: true,
  imports: [FormsModule, DateInputComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      @if (invoice(); as inv) {
        @if (nothingLeft()) {
          <section class="bo-panel">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title">
                {{ i18n.t('inv.credit.title', { n: inv.number }) }}
              </h2>
            </div>
            <div class="bo-panel__body">
              <p class="notice">{{ i18n.t('inv.view.fullyCredited') }}</p>
            </div>
            <div class="bo-panel__foot">
              <button
                type="button"
                class="btn-ghost"
                (click)="cancelled.emit({ id: inv.id, customerId: customerId() })"
              >
                {{ i18n.t('inv.back.account') }}
              </button>
            </div>
          </section>
        } @else {
          <section class="bo-panel">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title">
                {{ i18n.t('inv.credit.title', { n: inv.number }) }}
              </h2>
              <button
                type="button"
                class="btn-ghost bo-small"
                (click)="cancelled.emit({ id: inv.id, customerId: customerId() })"
              >
                {{ i18n.t('common.cancel') }}
              </button>
              <p class="bo-meta">
                <span>{{ i18n.t('common.customer') }}</span>
                <span>{{ customerName() }}</span>
                <span>{{ i18n.t('inv.totalTtc') }}</span>
                <strong class="mono">{{ i18n.money(inv.grandTotalTtc) }}</strong>
                <span>{{ i18n.t('inv.outstanding') }}</span>
                <strong class="mono">{{ i18n.money(inv.outstanding) }}</strong>
                @if (inv.credited > 0) {
                  <span>{{ i18n.t('inv.credited') }}</span>
                  <strong class="mono">{{ i18n.money(inv.credited) }}</strong>
                }
                <span [class]="'bo-state ' + INVOICE_TONE[inv.status]">{{ stateWord(inv.status) }}</span>
              </p>
              <p class="bo-panel__note">{{ i18n.t('inv.credit.note') }}</p>
            </div>

            <p class="notice warn">
              <strong>{{ i18n.t('inv.credit.irreversible') }}</strong>
              {{ i18n.t('inv.credit.irreversibleHint') }}
            </p>

            <div class="bo-panel__body bo-form">
              <div class="bo-group">
                <div class="bo-fields bo-fields--roomy">
                  <div class="bo-field bo-field--wide" [class.is-invalid]="touched.missing('reason', reason())">
                    <label for="cn-reason">
                      {{ i18n.t('inv.view.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <textarea
                      id="cn-reason"
                      rows="2"
                      maxlength="400"
                      [ngModel]="reason()"
                      (ngModelChange)="reason.set($event)"
                      (blur)="touched.touch('reason')"
                      [attr.aria-invalid]="touched.missing('reason', reason()) ? 'true' : null"
                      [attr.aria-describedby]="touched.missing('reason', reason()) ? 'cn-reason-error' : null"
                    ></textarea>
                    <p class="bo-hint">{{ i18n.t('inv.credit.reasonHint') }}</p>
                    @if (touched.missing('reason', reason())) {
                      <span id="cn-reason-error" class="bo-error" role="alert">{{
                        i18n.t('inv.problem.reasonRequired')
                      }}</span>
                    }
                  </div>
                  <div class="bo-field">
                    <label for="cn-issued">{{ i18n.t('inv.issuedOn') }}</label>
                    <app-date-input id="cn-issued" [ngModel]="issuedOn()" (ngModelChange)="issuedOn.set($event)" />
                  </div>
                </div>
              </div>

              <div class="bo-group">
                <h3 class="bo-group__title">
                  {{ i18n.t('inv.credit.scope') }}
                </h3>
                <nav class="bo-chips" [attr.aria-label]="i18n.t('inv.credit.scope')">
                  <button
                    type="button"
                    [class.is-on]="scope() === 'all'"
                    [attr.aria-current]="scope() === 'all' ? 'true' : null"
                    (click)="scope.set('all')"
                  >
                    {{ i18n.t('inv.credit.all') }}
                  </button>
                  <button
                    type="button"
                    [class.is-on]="scope() === 'some'"
                    [attr.aria-current]="scope() === 'some' ? 'true' : null"
                    (click)="scope.set('some')"
                  >
                    {{ i18n.t('inv.credit.some') }}
                  </button>
                </nav>
                @if (nothingToCredit()) {
                  <span class="bo-error" role="alert">{{ i18n.t('inv.problem.nothingToCredit') }}</span>
                }
              </div>
            </div>

            <div class="bo-scroll">
              <table class="bo-data" [attr.aria-label]="i18n.t('inv.credit.scope')">
                <thead>
                  <tr>
                    <th>{{ i18n.t('inv.view.item') }}</th>
                    <th class="num">{{ i18n.t('register.qty') }}</th>
                    <th class="num">{{ i18n.t('inv.view.creditedQty') }}</th>
                    <th class="num">{{ i18n.t('inv.credit.left') }}</th>
                    <th class="num">{{ i18n.t('rent.unitPrice') }}</th>
                    @if (hasDays()) {
                      <th class="num">{{ i18n.t('print.inv.days') }}</th>
                    }
                    <th>{{ i18n.t('receipt.rate') }}</th>
                    <th class="num">{{ i18n.t('inv.credit.qty') }}</th>
                    @if (hasStock()) {
                      <th class="num">{{ i18n.t('inv.credit.backToStock') }}</th>
                    }
                    <th class="num">{{ i18n.t('inv.credit.lineCredit') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of rows(); track row.line.id) {
                    <tr [class.is-muted]="row.left <= 0">
                      <td [attr.data-label]="i18n.t('inv.view.item')">
                        <span class="mono bo-soft">{{ row.line.sku }}</span>
                        <span>{{ row.line.name }}</span>
                        @if (row.left <= 0) {
                          <span class="bo-state">{{ i18n.t('inv.view.fullyCredited') }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('register.qty')">
                        {{ row.line.qty }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('inv.view.creditedQty')">
                        @if (row.line.creditedQty > 0) {
                          {{ row.line.creditedQty }}
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('inv.credit.left')">
                        {{ row.left }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('rent.unitPrice')">
                        {{ i18n.money(row.line.unitPrice) }}
                      </td>
                      @if (hasDays()) {
                        <td class="num mono" [attr.data-label]="i18n.t('print.inv.days')">
                          {{ (row.line.days ?? 0) > 0 ? row.line.days : '' }}
                        </td>
                      }
                      <td [attr.data-label]="i18n.t('receipt.rate')">
                        {{ rateText(row.line.taxRate) }}
                      </td>
                      <td class="num" [attr.data-label]="i18n.t('inv.credit.qty')">
                        @if (scope() === 'some' && row.left > 0) {
                          <input
                            type="number"
                            min="0"
                            [max]="row.left"
                            class="num mono bo-input-narrow"
                            [attr.id]="'cn-q' + row.line.id"
                            [attr.aria-label]="i18n.t('inv.credit.qty') + ' — ' + row.line.name"
                            [attr.aria-invalid]="qtyInvalid(row) ? 'true' : null"
                            [ngModel]="qtyOf(row.line.id)"
                            (ngModelChange)="setQty(row.line.id, $event)"
                            (blur)="touched.touch('q' + row.line.id)"
                          />
                          <button type="button" class="btn-ghost bo-small" (click)="setQty(row.line.id, row.left)">
                            {{ i18n.t('inv.credit.all') }}
                          </button>
                          @if (qtyInvalid(row)) {
                            <span class="bo-error" role="alert">{{
                              i18n.t('inv.problem.creditQtyRange', {
                                n: row.left,
                              })
                            }}</span>
                          }
                        } @else {
                          <span class="mono">{{ row.qtyToCredit }}</span>
                        }
                      </td>
                      @if (hasStock()) {
                        <td class="num" [attr.data-label]="i18n.t('inv.credit.backToStock')">
                          @if (row.out > 0 && row.qtyToCredit > 0) {
                            <input
                              type="number"
                              min="0"
                              [max]="row.backMax"
                              class="num mono bo-input-narrow"
                              [attr.id]="'cn-b' + row.line.id"
                              [attr.aria-label]="i18n.t('inv.credit.backToStock') + ' — ' + row.line.name"
                              [attr.aria-invalid]="backInvalid(row) ? 'true' : null"
                              [ngModel]="backOf(row.line.id)"
                              (ngModelChange)="setBack(row.line.id, $event)"
                              (blur)="touched.touch('b' + row.line.id)"
                            />
                            @if (backInvalid(row)) {
                              <span class="bo-error" role="alert">{{
                                i18n.t('inv.problem.backToStockRange', { n: row.backMax })
                              }}</span>
                            }
                          }
                        </td>
                      }
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('inv.credit.lineCredit')">
                        {{ i18n.money(row.credit) }}
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (hasStock()) {
              <p class="bo-hint bo-panel__note">{{ i18n.t('inv.credit.backToStockHint') }}</p>
            }

            <div class="bo-panel__body">
              <div class="bo-metrics">
                <div class="bo-metric bo-metric--lead">
                  <span class="bo-metric__label">{{ i18n.t('inv.credit.total') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(grandTotal()) }}</strong>
                  <span class="bo-metric__note"
                    ><span>{{ i18n.t('inv.credit.previewNote') }}</span></span
                  >
                </div>
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('inv.totalHt') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(subTotal()) }}</strong>
                </div>
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('inv.taxTotal') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(taxTotal()) }}</strong>
                </div>
                <div class="bo-metric">
                  <span class="bo-metric__label">{{ i18n.t('inv.totalTtc') }}</span>
                  <strong class="bo-metric__value mono">{{ i18n.money(grandTotal()) }}</strong>
                </div>
              </div>

              @if (exceeds() > 0) {
                <p class="notice warn">
                  {{
                    i18n.t('inv.credit.beyondOutstanding', {
                      v: i18n.money(exceeds()),
                    })
                  }}
                </p>
              }

              <div class="bo-check" [class.is-invalid]="ackMissing()">
                <input
                  id="cn-ack"
                  type="checkbox"
                  [ngModel]="acknowledged()"
                  (ngModelChange)="acknowledged.set($event)"
                  (blur)="touched.touch('ack')"
                  [attr.aria-invalid]="ackMissing() ? 'true' : null"
                  [attr.aria-describedby]="ackMissing() ? 'cn-ack-error' : null"
                />
                <label for="cn-ack">{{ i18n.t('inv.credit.ack') }}</label>
              </div>
              @if (ackMissing()) {
                <span id="cn-ack-error" class="bo-error" role="alert">{{ i18n.t('inv.problem.ackRequired') }}</span>
              }
            </div>

            <div class="bo-panel__foot">
              <button
                type="button"
                class="btn-ghost"
                (click)="cancelled.emit({ id: inv.id, customerId: customerId() })"
              >
                {{ i18n.t('common.cancel') }}
              </button>
              <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">
                {{ i18n.t('inv.credit.save') }}
              </button>
            </div>
          </section>
        }
      } @else if (notFound()) {
        <section class="bo-panel">
          <div class="bo-panel__body">
            <p class="notice danger" role="alert">
              {{ i18n.t('inv.view.notFound') }}
            </p>
          </div>
          <div class="bo-panel__foot">
            <button
              type="button"
              class="btn-ghost"
              (click)="cancelled.emit({ id: invoiceId(), customerId: customerId() })"
            >
              {{ i18n.t('inv.back.account') }}
            </button>
          </div>
        </section>
      } @else {
        <p class="bo-loading">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class ReceivableCreditNoteFormComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly regional = inject(RegionalService);

  readonly invoiceId = input.required<string>();
  readonly customerId = input.required<string>();
  readonly customers = input<AdminCustomer[]>([]);

  readonly saved = output<CreditNote>();
  readonly cancelled = output<DocRef>();

  readonly INVOICE_TONE = INVOICE_TONE;

  readonly touched = new TouchedFields();
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notFound = signal(false);
  readonly invoice = signal<Invoice | null>(null);

  readonly reason = signal('');
  readonly issuedOn = signal<string>(today(this.regional.settings().timeZone));
  readonly scope = signal<Scope>('all');
  /** Quantity typed per invoice line id; only read under the `some` scope. */
  readonly quantities = signal<Record<string, number>>({});
  /** P0.3: goods back into stock per invoice line id; 0 (a credit of money only) unless typed. */
  readonly backs = signal<Record<string, number>>({});
  readonly acknowledged = signal(false);

  readonly customerName = computed(
    () => this.customers().find((c) => c.id === this.customerId())?.fullName ?? this.invoice()?.customerName ?? '',
  );

  readonly rows = computed<CreditRow[]>(() => {
    const inv = this.invoice();
    if (!inv) return [];
    const decimals = this.regional.decimals();
    const some = this.scope() === 'some';
    const typed = this.quantities();
    const backs = this.backs();
    return inv.lines.map((line) => {
      const left = Math.max(0, line.qty - line.creditedQty);
      const qtyToCredit = some ? (typed[line.id] ?? 0) : left;
      // Pro-rata on the line's own net: a discounted line is credited net of its discount.
      const credit = line.qty > 0 ? roundTo((line.lineNet * qtyToCredit) / line.qty, decimals) : 0;
      const out = Math.max(0, (line.stockOutQty ?? 0) - (line.stockReturnedQty ?? 0));
      const back = out > 0 ? (backs[line.id] ?? 0) : 0;
      return { line, left, qtyToCredit, credit, out, back, backMax: Math.max(0, Math.min(qtyToCredit, out)) };
    });
  });

  /** A "back in stock" column only when a line of this invoice took goods out that have not all come back (P0.3). */
  readonly hasStock = computed(() =>
    (this.invoice()?.lines ?? []).some((l) => (l.stockOutQty ?? 0) - (l.stockReturnedQty ?? 0) > 0),
  );

  /** A days column only when a line is priced by the day. */
  readonly hasDays = computed(() => (this.invoice()?.lines ?? []).some((l) => (l.days ?? 0) > 0));

  readonly nothingLeft = computed(() => {
    const inv = this.invoice();
    return !!inv && inv.lines.every((l) => l.qty - l.creditedQty <= 0);
  });

  readonly subTotal = computed(() =>
    roundTo(
      this.rows().reduce((sum, r) => sum + r.credit, 0),
      this.regional.decimals(),
    ),
  );

  readonly taxTotal = computed(() =>
    taxByRate(
      this.rows()
        .filter((r) => r.qtyToCredit > 0)
        .map((r) => ({ lineNet: r.credit, taxRate: r.line.taxRate })),
      this.invoice()?.taxMode === 'Taxed',
      this.regional.decimals(),
    ),
  );

  readonly grandTotal = computed(() => roundTo(this.subTotal() + this.taxTotal(), this.regional.decimals()));

  /** How far this note goes past what is still owed — money the company would then owe the customer. */
  readonly exceeds = computed(() => {
    const inv = this.invoice();
    if (!inv) return 0;
    return roundTo(this.grandTotal() - Math.max(0, inv.outstanding), this.regional.decimals());
  });

  async ngOnInit(): Promise<void> {
    this.busy.set(true);
    try {
      this.invoice.set(await this.api.getInvoice(this.invoiceId()));
    } catch (e) {
      // The route answers 404 with an empty body, which the generic message renders as "HTTP 404".
      if (e instanceof ApiError && e.status === 404) this.notFound.set(true);
      else this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  // ---------- the quantities ----------

  qtyOf(lineId: string): number {
    return this.quantities()[lineId] ?? 0;
  }

  setQty(lineId: string, value: unknown): void {
    const qty = value === null || value === '' ? 0 : Number(value);
    this.quantities.update((q) => ({
      ...q,
      [lineId]: Number.isFinite(qty) ? qty : 0,
    }));
  }

  backOf(lineId: string): number {
    return this.backs()[lineId] ?? 0;
  }

  setBack(lineId: string, value: unknown): void {
    const qty = value === null || value === '' ? 0 : Number(value);
    this.backs.update((b) => ({ ...b, [lineId]: Number.isFinite(qty) ? qty : 0 }));
  }

  stateWord(status: Invoice['status']): string {
    const key = stateKey('inv.state', status);
    const word = this.i18n.t(key);
    return word === key ? status : word;
  }

  /** One string: the rate and its name, when the company gave it one. */
  rateText(rate: number): string {
    const label = this.i18n.rateLabel(rate);
    return label ? `${rate} % · ${label}` : `${rate} %`;
  }

  // ---------- what the server will refuse ----------

  /** A typed quantity is wrong when it is negative or more than the line has left. */
  qtyInvalid(row: CreditRow): boolean {
    const ok = row.qtyToCredit >= 0 && row.qtyToCredit <= row.left;
    return this.touched.missing('q' + row.line.id, ok ? 'ok' : '');
  }

  /** Goods back in stock are wrong below zero or above what this note credits and is still out. */
  backInvalid(row: CreditRow): boolean {
    return this.touched.missing('b' + row.line.id, backOk(row) ? 'ok' : '');
  }

  nothingToCredit(): boolean {
    const any = this.rows().some((r) => r.qtyToCredit > 0);
    return this.touched.missing('scope', any ? 'ok' : '');
  }

  ackMissing(): boolean {
    return this.touched.missing('ack', this.acknowledged() ? 'ok' : '');
  }

  private problem(): CreditProblem | null {
    if (!this.reason().trim()) return 'reasonRequired';
    const rows = this.rows();
    if (rows.some((r) => r.qtyToCredit < 0 || r.qtyToCredit > r.left)) return 'creditQtyRange';
    if (!rows.some((r) => r.qtyToCredit > 0)) return 'nothingToCredit';
    if (rows.some((r) => !backOk(r))) return 'backToStockRange';
    if (!this.acknowledged()) return 'ackRequired';
    return null;
  }

  // ---------- the save ----------

  async save(): Promise<void> {
    const inv = this.invoice();
    if (!inv) return;
    // A save was attempted, so every field may now say what is wrong with it.
    this.touched.touchAll('reason', 'scope', 'ack', ...inv.lines.flatMap((l) => ['q' + l.id, 'b' + l.id]));
    if (this.problem()) return;

    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('inv.credit.save'))))) return;

    const request: CreateCreditNoteRequest = {
      reason: this.reason().trim(),
      // A date field means a day; midday keeps it the same day west of Greenwich.
      issuedAtUtc: this.issuedOn() ? midday(this.issuedOn()) : null,
    };
    // Under `all` the lines are left out: the server then credits whatever is left of every line — unless goods come
    // back into stock, which only a line can say (P0.3); then every line still open is sent, as `all` would credit it.
    const anyBack = this.rows().some((r) => r.back > 0);
    if (this.scope() === 'some' || anyBack) {
      request.lines = this.rows()
        .filter((r) => r.qtyToCredit > 0)
        .map((r) =>
          r.back > 0
            ? { invoiceLineId: r.line.id, qty: r.qtyToCredit, stockReturnedQty: r.back }
            : { invoiceLineId: r.line.id, qty: r.qtyToCredit },
        );
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      const note = await this.api.createCreditNote(inv.id, request);
      this.saved.emit(note);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
      // The refusal may be about quantities that moved since the invoice was read.
      try {
        this.invoice.set(await this.api.getInvoice(inv.id));
      } catch {
        // The banner already says what went wrong; a failed re-read adds nothing.
      }
    } finally {
      this.busy.set(false);
    }
  }
}

/** 0 ≤ back ≤ min(credited on this note, still out of stock): the server's own rule (P0.3). */
function backOk(row: CreditRow): boolean {
  return row.back >= 0 && row.back <= row.backMax;
}
