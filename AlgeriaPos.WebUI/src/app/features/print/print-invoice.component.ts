import { Component, computed, input } from '@angular/core';
import { customerFiscalIdLines } from '../../core/customer-fiscal-ids';
import { taxByRate } from '../../core/document-tax';
import { invoiceLineName } from '../backoffice/maintenance/maintenance-words';
import { Invoice } from '../backoffice/receivables/receivables-api.service';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, partyLines } from './print-context';

/**
 * The sale invoice on A4, laid out as the company's own Facture N° 45 (owner, 2026-10-10).
 *
 * <p>The client under the date, with every fiscal identifier the buyer had at issue (RC, NIF, AI…);
 * one table — N°, désignation, quantité, prix, jours, total HT — where a hire line reads quantity ×
 * daily price × days and adds up as printed; then the total, the tax at each rate and the total
 * with tax; the amount in words ("Arrêté la présente facture à la somme de :"); the way of paying;
 * and "L'entreprise" with room for the stamp and the signature.</p>
 *
 * <p>What it states, and never nets: the per-rate tax is rebuilt with the same rule the server
 * used, so it agrees with the stored total to the centime. What was settled before the paper
 * existed (`prepaid`), given back (`credited`) and received is printed, with the balance due, only
 * when there is any of it — a fresh invoice says only what it bills. An untaxed invoice says so in
 * words and prints no rate; a company that wants the zero stated gets a 0.00 line (`showZeroTax`).
 * Columns that would say nothing on this invoice — days, discount, rate — are left out.</p>
 *
 * <p>The buyer block is the snapshot the invoice took at issue (name, address, tax id and the other
 * identifiers); the customer row supplies only what was not recorded then — code, phone, and the
 * identifiers of an invoice raised before they were kept — so a reprint matches the first print.</p>
 */
@Component({
  selector: 'app-print-invoice',
  standalone: true,
  imports: [PrintDocumentComponent],
  styles: [
    `
      .print-sentence--words {
        text-transform: uppercase;
      }
      .print-signatures--company {
        grid-template-columns: minmax(12rem, 18rem);
      }
      .print-table .print-line-no {
        inline-size: 2.5rem;
      }
    `,
  ],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          kind="invoice"
          [title]="c.t('inv.invoice')"
          titleKey="inv.invoice"
          [subtitle]="untaxed() ? c.t('receipt.withoutTax') : ''"
          [number]="invoice().number"
          [meta]="meta()"
          [breakBefore]="copy > 0"
        >
          <div class="print-parties">
            <div class="print-block">
              <span class="print-block__title">{{ c.t('print.inv.client') }}</span>
              @for (line of buyer(); track $index) {
                <div [class.print-line--lead]="!line.label">
                  @if (line.label) {
                    <span class="print-line__label">{{ line.label }}</span>
                  }
                  <span [class.mono]="line.ltr" [attr.dir]="line.ltr ? 'ltr' : null">{{ line.value }}</span>
                </div>
              }
            </div>
          </div>

          @if (source() || invoice().notes) {
            <ul class="print-meta">
              @if (source()) {
                <li><span class="print-line__label">{{ c.t('print.source') }}</span><span class="mono">{{ source() }}</span></li>
              }
              @if (invoice().notes) {
                <li><span class="print-line__label">{{ c.t('rent.notes') }}</span><span>{{ invoice().notes }}</span></li>
              }
            </ul>
          }

          <table class="print-table">
            <thead>
              <tr>
                <th class="print-line-no">{{ c.t('print.inv.lineNo') }}</th>
                <th>{{ c.t('print.inv.description') }}</th>
                <th class="num">{{ c.t('print.inv.qty') }}</th>
                <th class="num">{{ c.t('print.inv.price') }}</th>
                @if (hasDays()) {
                  <th class="num">{{ c.t('print.inv.days') }}</th>
                }
                @if (hasDiscount()) {
                  <th class="num">{{ c.t('inv.view.discount') }}</th>
                }
                @if (showsRate()) {
                  <th>{{ c.t('receipt.rate') }}</th>
                }
                <th class="num">{{ c.t('print.inv.lineTotal') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (line of invoice().lines; track line.id; let i = $index) {
                <tr>
                  <td class="num mono">{{ i + 1 }}</td>
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
                  @if (hasDiscount()) {
                    <td class="num mono">{{ line.discount > 0 ? c.money(line.discount) : '' }}</td>
                  }
                  @if (showsRate()) {
                    <td>{{ rateWord(line.taxRate) }}</td>
                  }
                  <td class="num mono">{{ c.money(line.lineNet) }}</td>
                </tr>
              }
            </tbody>
          </table>

          <table class="print-table print-table--compact">
            <tbody>
              @if (untaxed() && !showsZeroTax()) {
                <tr class="print-total">
                  <td>{{ c.t('print.inv.amountTotal') }}</td>
                  <td class="num mono">{{ c.money(invoice().grandTotalTtc) }}</td>
                </tr>
              } @else {
                <tr>
                  <td>{{ c.t('print.inv.amountTotal') }}</td>
                  <td class="num mono">{{ c.money(invoice().subTotalHt) }}</td>
                </tr>
                @if (untaxed()) {
                  <tr>
                    <td>{{ c.t('inv.taxTotal') }}</td>
                    <td class="num mono">{{ c.money(0) }}</td>
                  </tr>
                } @else {
                  @for (b of buckets(); track b.rate) {
                    <tr>
                      <td>
                        {{ c.t('print.inv.taxAt', { rate: b.rate }) }}
                        @if (buckets().length > 1) {
                          <span class="print-soft"> ({{ c.t('receipt.taxBase') }} <span class="mono">{{ c.money(b.base) }}</span>)</span>
                        }
                      </td>
                      <td class="num mono">{{ c.money(b.tax) }}</td>
                    </tr>
                  }
                }
                <tr class="print-total">
                  <td>{{ c.t('inv.totalTtc') }}</td>
                  <td class="num mono">{{ c.money(invoice().grandTotalTtc) }}</td>
                </tr>
              }
              @if (settled()) {
                @if (invoice().prepaid > 0) {
                  <tr>
                    <td>
                      {{ c.t('inv.prepaid') }}
                      @if (source()) {
                        <span class="print-soft"> ({{ source() }})</span>
                      }
                    </td>
                    <td class="num mono">{{ c.money(invoice().prepaid) }}</td>
                  </tr>
                }
                @if (invoice().credited > 0) {
                  <tr>
                    <td>{{ c.t('inv.credited') }}</td>
                    <td class="num mono">{{ c.money(invoice().credited) }}</td>
                  </tr>
                }
                @if (invoice().received > 0) {
                  <tr>
                    <td>{{ c.t('inv.received') }}</td>
                    <td class="num mono">{{ c.money(invoice().received) }}</td>
                  </tr>
                }
                <tr class="print-total">
                  <td>{{ c.t('print.balanceDue') }}</td>
                  <td class="num mono">{{ c.money(invoice().outstanding) }}</td>
                </tr>
              }
            </tbody>
          </table>

          @if (invoice().claimed > 0) {
            <p class="print-sentence">
              {{ c.t('inv.claimed') }} : <span class="mono">{{ c.money(invoice().claimed) }}</span> — {{ c.t('inv.claimedHint') }}
            </p>
          }

          @if (words()) {
            <p class="print-sentence print-sentence--strong">{{ c.t('print.inv.inWords') }}</p>
            <p class="print-sentence print-sentence--words">{{ words() }}</p>
          }

          @if (paymentMode()) {
            <p class="print-sentence print-sentence--strong">
              <span>{{ c.t('print.inv.paymentMode') }} : </span><span class="print-sentence--words">{{ paymentMode() }}</span>
            </p>
          }

          @if (c.documents?.bankDetails) {
            <div class="print-block">
              <span class="print-block__title">{{ c.t('store.bank') }}</span>
              <div class="mono" style="white-space: pre-line">{{ c.documents?.bankDetails }}</div>
            </div>
          }

          <div class="print-signatures print-signatures--company">
            <div class="print-signature">{{ c.t('print.inv.forCompany') }}</div>
          </div>
        </app-print-sheet>
      }
    }
  `,
})
export class PrintInvoiceComponent {
  readonly ctx = input.required<PrintContext>();
  readonly invoice = input.required<Invoice>();
  readonly copies = input(1);

  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));
  readonly untaxed = computed(() => this.invoice().taxMode === 'Untaxed');
  readonly showsZeroTax = computed(() => this.ctx().regional.showZeroTax === true);

  /** A days column only when a line is priced by the day; a sale prints the four columns it always did. */
  readonly hasDays = computed(() => this.invoice().lines.some((l) => (l.days ?? 0) > 0));
  readonly hasDiscount = computed(() => this.invoice().lines.some((l) => l.discount > 0));
  /** The rate per line only when the lines differ: one rate is said once, on the tax line. */
  readonly showsRate = computed(() => !this.untaxed() && new Set(this.invoice().lines.map((l) => l.taxRate)).size > 1);

  /** Anything already settled, given back or received: only then does the paper print the balance due. */
  readonly settled = computed(() => {
    const inv = this.invoice();
    return inv.prepaid > 0 || inv.credited > 0 || inv.received > 0;
  });

  readonly customer = computed(() => this.ctx().customers.find((x) => x.id === this.invoice().customerId) ?? null);

  /** Name, code, address, phone, then every fiscal identifier in the profile's order. */
  readonly buyer = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    const inv = this.invoice();
    const customer = this.customer();
    // The tax id is printed with the other identifiers, under its own code, not by partyLines.
    const lines = partyLines(c, customer, { name: inv.buyerName, address: inv.buyerAddress, taxId: '' });
    const taxId = inv.buyerTaxId ?? customer?.taxId ?? null;
    const others = inv.buyerFiscalIds ?? customer?.fiscalIds ?? null;
    for (const id of customerFiscalIdLines(c.regional, taxId, others)) {
      lines.push({ label: id.code, value: id.value, ltr: true });
    }
    return lines;
  });

  /** The date, and the due date only when it is not the same day: "payable on receipt" says nothing more. */
  readonly meta = computed<LabelledLine[]>(() => {
    const c = this.ctx();
    const inv = this.invoice();
    const issued = c.day(inv.issuedAtUtc);
    const due = c.day(inv.dueDateUtc);
    const lines: LabelledLine[] = [{ label: c.t('print.inv.date'), value: issued }];
    if (due !== issued) lines.push({ label: c.t('inv.dueOn'), value: due });
    return lines;
  });

  /** "Sale T-0042" / "Issue note ISS-000007": the paper this invoice bills, when it bills one. */
  readonly source = computed(() => {
    const inv = this.invoice();
    if (inv.origin === 'Direct' || !inv.sourceType) return '';
    return this.ctx().t('print.source.' + inv.sourceType, { n: inv.sourceNumber ?? '—' });
  });

  /** Per-rate tax, computed the way `DocumentTax.On` does, so it agrees with `taxTotal`. */
  readonly buckets = computed(() => (this.untaxed() ? [] : taxByRate(this.invoice().lines, this.ctx().regional.currencyDecimals)));

  readonly words = computed(() => this.ctx().words(this.invoice().grandTotalTtc));

  /** The way of paying in the document language — "Chèque" — or the stored key for a method since removed. */
  readonly paymentMode = computed(() => {
    const key = this.invoice().paymentMethodKey;
    if (!key) return '';
    const method = this.ctx().methods.find((m) => m.key === key);
    return method ? this.ctx().name(method) : key;
  });

  /** "19 % Taux normal", or just the figure when the profile names no label. */
  /** MS-34: a job invoice's parts or labour line in the paper's language; any other line as stored. */
  lineName(name: string): string {
    return invoiceLineName(name, (key, params) => this.ctx().t(key, params));
  }

  rateWord(rate: number): string {
    const label = this.ctx().rateLabel(rate);
    return label ? `${rate} % ${label}` : `${rate} %`;
  }
}
