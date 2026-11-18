import { Component, computed, input } from '@angular/core';
import { taxByRate } from '../../core/document-tax';
import { invoiceLineName } from '../backoffice/maintenance/maintenance-words';
import { CreditNote, Invoice } from '../backoffice/receivables/receivables-api.service';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, branchLine, partyLines } from './print-context';

/**
 * The credit note on A4 (docs/07 §4.4): which invoice it corrects, why, the pro-rata lines it
 * gives back, and the totals under a caption that says the money is to the customer's credit.
 *
 * <p>It takes no buyer snapshot of its own — it references the invoice — so the buyer block is
 * the invoice's frozen one when the invoice could be read, else the customer row.</p>
 */
@Component({
  selector: 'app-print-credit-note',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          kind="creditNote"
          [title]="c.t(titleKey())"
          [titleKey]="titleKey()"
          [number]="note().number"
          [meta]="meta()"
          [breakBefore]="copy > 0"
        >
          <div class="print-parties">
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.invoice.buyer') }}</span>
              @for (line of buyer(); track $index) {
                <div [class.print-line--lead]="!line.label">
                  @if (line.label) {
                    <span class="print-line__label">{{ line.label }}</span>
                  }
                  <span [class.mono]="!!line.label" [attr.dir]="line.ltr ? 'ltr' : null">{{ line.value }}</span>
                </div>
              }
            </div>
            <div class="print-block">
              <span class="print-block__title">{{ c.t('rent.branch') }}</span>
              <div>{{ branch() || '—' }}</div>
              <div>
                <span class="print-line__label">{{ c.t('inv.view.reason') }}</span>
                <span>{{ note().reason }}</span>
              </div>
            </div>
          </div>

          <p class="print-sentence print-sentence--strong">{{ c.t('print.crn.corrects', { n: note().invoiceNumber ?? '—' }) }}</p>

          <table class="print-table">
            <thead>
              <tr>
                <th>{{ c.t('inv.view.item') }}</th>
                <th class="num">{{ c.t('register.qty') }}</th>
                <th class="num">{{ c.t('inv.view.unitPrice') }}</th>
                @if (hasDays()) {
                  <th class="num">{{ c.t('print.inv.days') }}</th>
                }
                @if (taxed()) {
                  <th>{{ c.t('receipt.rate') }}</th>
                }
                <th class="num">{{ c.t('inv.view.lineNet') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (line of note().lines; track line.id) {
                <tr>
                  <td>
                    <span>{{ lineName(line.name) }}</span>
                    @if (line.sku) {
                      <span class="print-soft mono"> {{ line.sku }}</span>
                    }
                  </td>
                  <td class="num mono">{{ c.qty(line.qty) }}</td>
                  <td class="num mono">{{ c.money(line.unitPrice) }}</td>
                  @if (hasDays()) {
                    <td class="num mono">{{ line.days ?? '' }}</td>
                  }
                  @if (taxed()) {
                    <td>{{ rateWord(line.taxRate) }}</td>
                  }
                  <td class="num mono">{{ c.money(line.lineNet) }}</td>
                </tr>
              }
            </tbody>
          </table>

          @if (taxed() && buckets().length) {
            <table class="print-table print-table--compact">
              <caption>{{ c.t('register.tax') }}</caption>
              <thead>
                <tr>
                  <th>{{ c.t('receipt.rate') }}</th>
                  <th class="num">{{ c.t('receipt.taxBase') }}</th>
                  <th class="num">{{ c.t('register.tax') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (b of buckets(); track b.rate) {
                  <tr>
                    <td>{{ rateWord(b.rate) }}</td>
                    <td class="num mono">{{ c.money(b.base) }}</td>
                    <td class="num mono">{{ c.money(b.tax) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }

          <table class="print-table print-table--compact">
            <caption>{{ c.t('print.crn.credit') }}</caption>
            <tfoot>
              <tr>
                <td>{{ c.t('inv.totalHt') }}</td>
                <td class="num mono">{{ c.money(note().subTotalHt) }}</td>
              </tr>
              @if (taxed()) {
                <tr>
                  <td>{{ c.t('inv.taxTotal') }}</td>
                  <td class="num mono">{{ c.money(note().taxTotal) }}</td>
                </tr>
              }
              <tr class="print-total">
                <td>{{ c.t('inv.totalTtc') }}</td>
                <td class="num mono">{{ c.money(note().grandTotalTtc) }}</td>
              </tr>
            </tfoot>
          </table>

          @if (words()) {
            <p class="print-sentence print-sentence--strong">{{ c.t('print.inWords') }} {{ words() }}</p>
          }
        </app-print-sheet>
      }
    }
  `,
})
export class PrintCreditNoteComponent {
  readonly ctx = input.required<PrintContext>();
  readonly note = input.required<CreditNote>();
  /** The corrected invoice, when it could be read: its frozen buyer block is the one to print. */
  readonly invoice = input<Invoice | null>(null);
  readonly copies = input(1);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));
  /** A credit note over an untaxed invoice carries no tax; the server stores 0 and the paper says nothing about it. */
  readonly titleKey = computed(() => this.invoice()?.taxMode === 'Untaxed' ? 'rent.usage.internal' : 'print.crn.title');
  readonly taxed = computed(() => this.note().taxTotal > 0 || this.note().lines.some((l) => l.taxRate > 0));
  /** A hire line inherits its days from the invoice line, so quantity × price × days reads as it did there. */
  readonly hasDays = computed(() => this.note().lines.some((l) => (l.days ?? 0) > 0));

  readonly customer = computed(() => this.ctx().customers.find((x) => x.id === this.note().customerId) ?? null);
  readonly buyer = computed(() => {
    const inv = this.invoice();
    return partyLines(this.ctx(), this.customer(), {
      name: inv?.buyerName,
      address: inv?.buyerAddress,
      taxId: inv?.buyerTaxId,
    });
  });
  readonly branch = computed(() => branchLine(this.ctx(), this.note().branchId));

  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    const lines: LabelledLine[] = [{ label: c.t('inv.issuedOn'), value: c.day(this.note().issuedAtUtc) }];
    const inv = this.invoice();
    if (inv) lines.push({ label: c.t('inv.credit.onInvoice'), value: `${inv.number} · ${c.day(inv.issuedAtUtc)}` });
    return lines;
  });

  readonly buckets = computed(() => (this.taxed() ? taxByRate(this.note().lines, this.ctx().regional.currencyDecimals) : []));
  readonly words = computed(() => this.ctx().words(this.note().grandTotalTtc));

  /** MS-34: a job invoice's parts or labour line in the paper's language; any other line as stored. */
  lineName(name: string): string {
    return invoiceLineName(name, (key, params) => this.ctx().t(key, params));
  }

  rateWord(rate: number): string {
    const label = this.ctx().rateLabel(rate);
    return label ? `${rate} % ${label}` : `${rate} %`;
  }
}
