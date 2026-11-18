import { Component, computed, input } from '@angular/core';
import { ReceiptVoucher } from '../backoffice/receivables/receivables-api.service';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, branchLine, partyLines } from './print-context';

/**
 * The receipt voucher on A5, two copies — company and customer (docs/07 §4.4, §8 accepted).
 *
 * <p>Printable while the voucher is still <b>pending</b>, which is when the customer needs it:
 * they have just handed over a cheque. The paper then says so in words — subject to
 * collection, settles nothing until confirmed — and never says "paid". A confirmed voucher says
 * when it was collected; a bounced one says it bounced and carries the note.</p>
 */
@Component({
  selector: 'app-print-receipt-voucher',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          kind="receiptVoucher"
          [title]="c.t('print.rcpt.title')"
          titleKey="print.rcpt.title"
          [number]="voucher().number"
          [meta]="meta()"
          [copyLabel]="copyLabel(copy)"
          [breakBefore]="copy > 0"
          [a5]="true"
        >
          <div class="print-parties">
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.rcpt.receivedFrom') }}</span>
              @for (line of payer(); track $index) {
                <div [class.print-line--lead]="!line.label">
                  @if (line.label) {
                    <span class="print-line__label">{{ line.label }}</span>
                  }
                  <span [class.mono]="!!line.label" [attr.dir]="line.ltr ? 'ltr' : null">{{ line.value }}</span>
                </div>
              }
            </div>
            <div class="print-block">
              <span class="print-block__title">{{ c.t('pay.amount') }}</span>
              <div class="print-line--lead mono">{{ c.money(voucher().amount) }}</div>
              @if (words()) {
                <div>{{ c.t('print.rcpt.inWords') }} {{ words() }}</div>
              }
              <div><span class="print-line__label">{{ c.t('inv.rcpt.method') }}</span><span>{{ method() }}</span></div>
              @if (voucher().instrumentNumber) {
                <div><span class="print-line__label">{{ c.t('inv.rcpt.instrumentNumber') }}</span><span class="mono">{{ voucher().instrumentNumber }}</span></div>
              }
              @if (voucher().instrumentBank) {
                <div><span class="print-line__label">{{ c.t('pay.instrumentBank') }}</span><span>{{ voucher().instrumentBank }}</span></div>
              }
              @if (voucher().instrumentDueDate) {
                <div><span class="print-line__label">{{ c.t('pay.instrumentDueDate') }}</span><span class="mono">{{ c.day(voucher().instrumentDueDate) }}</span></div>
              }
              @if (voucher().reference) {
                <div><span class="print-line__label">{{ c.t('treasury.reference') }}</span><span class="mono">{{ voucher().reference }}</span></div>
              }
            </div>
          </div>

          @if (voucher().purpose) {
            <p class="print-sentence print-sentence--strong">
              <span class="print-line__label">{{ c.t('print.rcpt.inExchangeFor') }}</span>&ngsp;<bdi>{{ voucher().purpose }}</bdi>
            </p>
          }

          <!-- What the money is for and the contract it belongs to (owner, 2026-10-02, answers 19, 20). -->
          @if (voucher().contractNumber || voucher().returnNoteNumber) {
            <ul class="print-meta">
              @if (voucher().kind && voucher().kind !== 'Payment') {
                <li><span class="print-line__label">{{ c.t('inv.rcpt.form.kind') }}</span><span>{{ c.t('inv.rcpt.kind.' + voucher().kind) }}</span></li>
              }
              @if (voucher().contractNumber) {
                <li><span class="print-line__label">{{ c.t('print.pv.contract') }}</span><span class="mono">{{ voucher().contractNumber }}</span></li>
              }
              @if (voucher().returnNoteNumber) {
                <li><span class="print-line__label">{{ c.t('print.pv.returnNote') }}</span><span class="mono">{{ voucher().returnNoteNumber }}</span></li>
              }
            </ul>
          }

          <table class="print-table">
            <caption>{{ c.t('inv.rcpt.view.allocations') }}</caption>
            <thead>
              <tr>
                <th>{{ c.t('inv.invoice') }}</th>
                <th class="num">{{ c.t('pay.amount') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (a of voucher().allocations; track a.invoiceId) {
                <tr>
                  <td class="mono">{{ a.invoiceNumber || '—' }}</td>
                  <td class="num mono">{{ c.money(a.amount) }}</td>
                </tr>
              }
              @if (voucher().unallocated > 0) {
                <tr>
                  <td>{{ c.t('inv.rcpt.unallocated') }}</td>
                  <td class="num mono">{{ c.money(voucher().unallocated) }}</td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr class="print-total">
                <td>{{ c.t('common.total') }}</td>
                <td class="num mono">{{ c.money(voucher().amount) }}</td>
              </tr>
            </tfoot>
          </table>

          <div class="print-notice">
            @switch (voucher().status) {
              @case ('Pending') {
                <strong>{{ c.t('print.rcpt.pending') }}</strong>
                {{ c.t('inv.claimedHint') }}
              }
              @case ('Confirmed') {
                <strong>{{ c.t('print.rcpt.confirmedOn', { d: c.day(voucher().confirmedAtUtc) }) }}</strong>
              }
              @case ('Bounced') {
                <strong>{{ c.t('print.rcpt.bouncedOn', { d: c.day(voucher().confirmedAtUtc) }) }}</strong>
                {{ voucher().statusNote }}
              }
            }
          </div>

          @if (voucher().notes) {
            <p class="print-sentence"><span class="print-line__label">{{ c.t('rent.notes') }}</span> {{ voucher().notes }}</p>
          }

          <div class="print-signatures">
            <div class="print-signature">{{ c.t('print.rcpt.receivedBy') }}</div>
          </div>
        </app-print-sheet>
      }
    }
  `,
})
export class PrintReceiptVoucherComponent {
  readonly ctx = input.required<PrintContext>();
  readonly voucher = input.required<ReceiptVoucher>();
  /** Two by default: the company keeps one, the customer takes one. */
  readonly copies = input(2);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));

  readonly customer = computed(() => this.ctx().customers.find((x) => x.id === this.voucher().customerId) ?? null);
  readonly payer = computed(() => partyLines(this.ctx(), this.customer(), { name: this.voucher().customerName }));
  readonly branch = computed(() => branchLine(this.ctx(), this.voucher().branchId));
  readonly words = computed(() => this.ctx().words(this.voucher().amount));

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    const lines: LabelledLine[] = [{ label: c.t('inv.rcpt.receivedOn'), value: c.day(this.voucher().receivedAtUtc) }];
    if (this.branch()) lines.push({ label: c.t('rent.branch'), value: this.branch() });
    return lines;
  });

  /** The tender's name in the document language; a key this session did not load prints raw. */
  readonly method = computed(() => {
    const c = this.ctx();
    const method = c.methods.find((m) => m.key === this.voucher().methodKey);
    return method ? c.name(method) : this.voucher().methodKey;
  });

  /** Company copy first, customer copy second; a third and later copy is unlabelled. */
  copyLabel(index: number): string {
    const c = this.ctx();
    if (this.copies() < 2) return '';
    return index === 0 ? c.t('print.rcpt.copy.company') : index === 1 ? c.t('print.rcpt.copy.customer') : '';
  }
}
