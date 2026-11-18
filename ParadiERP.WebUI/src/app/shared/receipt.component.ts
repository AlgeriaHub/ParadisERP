import { Component, OnDestroy, OnInit, computed, inject, input } from '@angular/core';
import { I18nService } from '../core/i18n.service';
import { Customer, Sale, Store } from '../core/models';
import { roundMoney } from '../core/money';
import { RegionalService } from '../core/regional.service';
import { cardCodeHash, earnedPoints, pointsForAmount } from '../core/loyalty';
import { PosService } from '../core/pos.service';
import { BranchFace } from '../core/branch-face';
import { documentIssuer } from '../core/document-issuer';

/**
 * 80 mm thermal receipt. Printing goes through the browser's own dialog (window.print)
 * so any printer the machine already has — thermal or A4 — works with no driver plumbing.
 * Which blocks print, their order and the legal wording all come from the company's
 * country profile.
 */
@Component({
  selector: 'app-receipt',
  standalone: true,
  template: `
    <div id="receipt-print" class="receipt mono">
      @if (sale().status === 'Refunded') {
        <div class="center refund-banner">{{ i18n.t('receipt.refund') }}</div>
        <hr />
      }

      <!-- Blocks print in the order the company's country profile lists them. -->
      @for (section of sections(); track section) {
        @switch (section) {
          @case ('header') {
            <div class="center head">
              @if (shopName()) {
                <strong class="shop">{{ shopName() }}</strong>
              }
              @if (issuer().address) {
                <div>{{ issuer().address }}</div>
              }
              @if (issuer().phone) {
                <div>{{ i18n.t('receipt.phone') }}: {{ issuer().phone }}</div>
              }
            </div>
          }
          @case ('fiscalIds') {
            @if (fiscalIds().length) {
              <div class="ids">
                @for (id of fiscalIds(); track id.code) {
                  <span>{{ id.code }}: {{ id.value }}</span>
                }
              </div>
            }
          }
          @case ('meta') {
            <hr />
            <div class="meta">
              <div>
                <span>{{ i18n.t('receipt.no') }}</span><b>{{ sale().reference }}</b>
              </div>
              <div>
                <span>{{ i18n.t('receipt.date') }}</span><b>{{ i18n.dateTime(sale().soldAtUtc) }}</b>
              </div>
              <div>
                <span>{{ i18n.t('receipt.cashier') }}</span><b>{{ sale().cashierName }}</b>
              </div>
              @if (customer()) {
                <div>
                  <span>{{ i18n.t('register.customer') }}</span><b>{{ customer()?.fullName }}</b>
                </div>
              }
            </div>
          }
          @case ('lines') {
            <hr />
            <table class="lines">
              <tbody>
                @for (line of sale().lines; track line.id) {
                  <tr>
                    <td colspan="2" class="name">{{ line.nameSnapshot }}</td>
                  </tr>
                  <tr>
                    <td class="qty">
                      {{ line.qty }} × {{ i18n.money(line.unitPrice) }}
                      @if (line.taxRate) {
                        <span class="rate">({{ line.taxRate }}%)</span>
                      }
                    </td>
                    <td class="amt">{{ i18n.money(line.lineTotalTtc) }}</td>
                  </tr>
                  @if (line.lineDiscount > 0) {
                    <tr class="disc">
                      <td>{{ i18n.t('register.discount') }}</td>
                      <td class="amt">-{{ i18n.money(line.lineDiscount) }}</td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          }
          @case ('totals') {
            <hr />
            <div class="totals">
              <div>
                <span>{{ i18n.t('register.subtotal') }}</span><b>{{ i18n.money(sale().subTotalHt) }}</b>
              </div>
              @if (sale().discountTotal > 0) {
                <div>
                  <span>{{ i18n.t('register.discount') }}</span><b>-{{ i18n.money(sale().discountTotal) }}</b>
                </div>
              }
              <div class="grand">
                <span>{{ i18n.t('register.total') }}</span><b>{{ i18n.money(sale().grandTotalTtc) }}</b>
              </div>
            </div>
          }
          @case ('taxBreakdown') {
            <div class="totals">
              @if (sale().taxMode === 'Untaxed' && showsZeroTax()) {
                <!-- Asked for by name: some buyers want the zero stated rather than absent,
                     so they can see tax was considered and not forgotten. -->
                <div class="tax"><span>{{ i18n.t('register.tax') }}</span><b>{{ i18n.money(0) }}</b></div>
              }
              @if (sale().taxMode === 'Untaxed') {
                <!-- Said in words. An empty tax block reads as an oversight; this reads as
                     a decision, which is what it was. -->
                <div class="tax"><span>{{ i18n.t('receipt.withoutTax') }}</span></div>
              }
              @for (bucket of taxBuckets(); track bucket.rate) {
                <div class="tax">
                  <span>
                    {{ i18n.t('register.tax') }} {{ bucket.rate }}%{{ i18n.rateLabel(bucket.rate) ? ' ' + i18n.rateLabel(bucket.rate) : '' }}
                    ({{ i18n.t('receipt.taxBase') }} {{ i18n.money(bucket.ht) }})
                  </span>
                  <b>{{ i18n.money(bucket.tax) }}</b>
                </div>
              }
            </div>
          }
          @case ('payment') {
            <div class="totals">
              @if (rounding()) {
                <div>
                  <span>{{ i18n.t('pay.rounding') }}</span><b>{{ rounding() > 0 ? '+' : '' }}{{ i18n.money(rounding()) }}</b>
                </div>
                <div class="grand">
                  <span>{{ i18n.t('pay.due') }}</span><b>{{ i18n.money(sale().grandTotalTtc + rounding()) }}</b>
                </div>
              }
              @if (sale().payments.length > 1) {
                @for (part of tendered(); track part.method) {
                  <div>
                    <span>{{ i18n.t('pay.' + part.method.toLowerCase()) }}</span><b>{{ i18n.money(part.amount) }}</b>
                  </div>
                }
              } @else {
                <div>
                  <span>{{ paymentLabel() }}</span><b>{{ i18n.money(sale().paidAmount) }}</b>
                </div>
              }
              @if (sale().changeAmount > 0) {
                <div>
                  <span>{{ i18n.t('pay.change') }}</span><b>{{ i18n.money(sale().changeAmount) }}</b>
                </div>
              }
              @for (card of cardBalances(); track card.code) {
                <div>
                  <span>{{ i18n.t('receipt.giftCardLeft', { code: card.code }) }}</span><b>{{ i18n.money(card.balance) }}</b>
                </div>
              }
              @if (points(); as pts) {
                @if (pts.redeemed) {
                  <div><span>{{ i18n.t('receipt.pointsRedeemed') }}</span><b>{{ pts.redeemed }}</b></div>
                }
                @if (pts.earned) {
                  <div><span>{{ i18n.t(sale().status === 'Refunded' ? 'receipt.pointsRemoved' : 'receipt.pointsEarned') }}</span><b>{{ pts.earned }}</b></div>
                }
              }
            </div>
          }
          @case ('legal') {
            @if (legalText()) {
              <hr />
              <div class="center legal">{{ legalText() }}</div>
            }
          }
          @case ('footer') {
            <hr />
            <div class="center foot">{{ store()?.receiptFooter || i18n.t('receipt.thanks') }}</div>
          }
        }
      }

      @if (sale().outboxState !== 'synced') {
        <div class="center pending">* {{ i18n.t('receipt.notSynced') }} *</div>
      }
    </div>
  `,
  styles: [
    `
      /* On screen this is a sheet of paper lying on the holder's ground: white, lifted, no
         border — a dashed rectangle read as a placeholder rather than as a slip. On thermal
         paper none of that exists, so the print block strips it back to bare text and lets
         the printer supply the edges. */
      .receipt {
        width: 300px;
        max-width: 100%;
        background: #fff;
        color: #000;
        padding: 14px;
        font-size: 12px;
        line-height: 1.4;
        border: none;
        box-shadow: 0 1px 2px rgba(15, 23, 42, 0.08), 0 8px 20px rgba(15, 23, 42, 0.1);
      }

      @media print {
        .receipt {
          box-shadow: none;
          padding: 0;
          width: auto;
        }
      }

      .center {
        text-align: center;
      }

      .shop {
        display: block;
        font-size: 14px;
      }

      .ids {
        display: flex;
        flex-wrap: wrap;
        gap: 0 8px;
        justify-content: center;
        font-size: 10px;
        margin-top: 3px;
      }

      .legal {
        white-space: pre-line;
        font-weight: 600;
      }

      hr {
        border: none;
        border-top: 1px dashed #000;
        margin: 6px 0;
      }

      .meta div,
      .totals div {
        display: flex;
        justify-content: space-between;
        gap: 8px;
      }

      .meta span,
      .totals span {
        color: #333;
      }

      .totals b {
        white-space: nowrap;
      }

      table.lines {
        width: 100%;
      }

      table.lines td {
        border: none;
        padding: 0;
      }

      .name {
        font-weight: 600;
        padding-top: 3px !important;
      }

      .qty {
        color: #333;
      }

      .rate {
        font-size: 10px;
      }

      .amt {
        text-align: end;
        white-space: nowrap;
      }

      .disc td {
        font-size: 11px;
      }

      .tax {
        font-size: 11px;
      }

      .grand {
        font-size: 15px;
        font-weight: 700;
        border-top: 1px solid #000;
        border-bottom: 1px solid #000;
        padding: 3px 0;
        margin: 3px 0;
      }

      .foot {
        font-size: 11px;
      }

      /* "Not yet sent" is a fact about this slip, so it is boxed rather than left as one
         more line of body copy. The asterisks stay: they are what survives a printer with
         no rules and no greys. */
      .pending {
        margin-top: 6px;
        padding: 3px 0;
        border: 1px dashed #000;
        font-weight: 700;
      }

      .refund-banner {
        font-weight: 700;
        letter-spacing: 0.05em;
        font-size: 13px;
      }
    `,
  ],
})
export class ReceiptComponent implements OnInit, OnDestroy {
  readonly i18n = inject(I18nService);
  // Public because the template reads it: the tax block asks the country profile whether a
  // zero tax line is printed. A private member compiles under `tsc --noEmit`, which does not
  // look at templates, and only fails in the AOT production build.
  readonly regional = inject(RegionalService);

  readonly sale = input.required<Sale>();
  readonly store = input<Store | null>(null);
  readonly customer = input<Customer | null>(null);

  private readonly pos = inject(PosService);
  private readonly face = inject(BranchFace);

  /**
   * Who the slip is from (owner, 2026-10-20): the till's branch, each field taken from the main
   * branch where the branch has none, then from the company settings. Read from the synced copy,
   * so an offline till prints it too.
   */
  readonly issuer = computed(() =>
    documentIssuer(
      this.face.till()?.id,
      this.face.all(),
      this.store(),
      this.i18n.lang(),
      this.regional.settings().fiscalIds.filter((id) => id.showOnReceipt),
    ),
  );

  readonly shopName = computed(() => this.issuer().name);

  /** Remaining balance of the gift cards used, from this till's replica (estimate until sync). */
  readonly cardBalances = computed(() => {
    const codes = [...new Set(this.sale().payments.filter((p) => p.method === 'GiftCard' && p.reference).map((p) => p.reference!))];
    return codes
      .map((code) => this.pos.giftCards().find((c) => c.codeHash === cardCodeHash(code)))
      .filter((c) => !!c)
      .map((c) => ({ code: c!.code, balance: c!.balance }));
  });

  /** Loyalty points for this sale, as the server will compute them. */
  readonly points = computed(() => {
    const sale = this.sale();
    if (!sale.customerId) return null;
    const program = this.pos.loyalty();
    const redeemed = pointsForAmount(program, sale.payments.filter((p) => p.method === 'Loyalty').reduce((s, p) => s + p.amount, 0));
    const earned = earnedPoints(program, sale.grandTotalTtc, sale.payments, sale.status === 'Refunded');
    return redeemed || earned ? { redeemed, earned } : null;
  });

  readonly sections = computed(() => this.regional.settings().receiptSections);

  /**
   * Whether to print a tax line on an untaxed document. Separate from whether tax applies:
   * some buyers want the zero stated so they can see it was considered, not forgotten.
   */
  readonly showsZeroTax = computed(() => this.regional.settings().showZeroTax === true);
  readonly legalText = computed(() => this.i18n.text(this.regional.settings().receiptLegalText));
  readonly rounding = computed(() => this.sale().roundingAdjustment ?? 0);

  /**
   * A split payment as the customer handed it over: card and credit as recorded, cash as
   * tendered (what was kept plus the change given back).
   */
  readonly tendered = computed(() => {
    const sale = this.sale();
    const r = (v: number) => roundMoney(v, this.regional.decimals());
    const byMethod = new Map<string, number>();
    for (const p of sale.payments) byMethod.set(p.method, r((byMethod.get(p.method) ?? 0) + p.amount));
    if (byMethod.has('Cash')) byMethod.set('Cash', r(byMethod.get('Cash')! + sale.changeAmount));
    return [...byMethod].map(([method, amount]) => ({ method, amount }));
  });

  readonly fiscalIds = computed(() => this.issuer().fiscalIds.map((id) => ({ code: id.label, value: id.value })));

  /** Rebuilds the by-rate tax table from the sale lines, so reprints match the original. */
  readonly taxBuckets = computed(() => {
    const r = (v: number) => roundMoney(v, this.regional.decimals());
    const map = new Map<number, { rate: number; ht: number; tax: number }>();
    for (const line of this.sale().lines) {
      if (!line.taxRate) continue;
      const bucket = map.get(line.taxRate) ?? { rate: line.taxRate, ht: 0, tax: 0 };
      bucket.tax = r(bucket.tax + line.lineTaxAmount);
      bucket.ht = r(bucket.ht + (line.lineTotalTtc - line.lineTaxAmount));
      map.set(line.taxRate, bucket);
    }
    return [...map.values()].sort((a, b) => b.rate - a.rate);
  });

  readonly paymentLabel = computed(() => {
    const method = this.sale().paymentMethod;
    return method === 'Mixed' ? this.i18n.t('pay.mixed') : this.i18n.t('pay.' + method.toLowerCase());
  });

  // The 80 mm page rule lives here, not in styles.scss: an @page cannot be scoped to a
  // selector, and declared globally it made every back-office Print button produce one blank
  // strip (docs/07 §4.1). Injected while the receipt is on the page, removed with it, so the
  // A4 default in styles.scss is what everything else prints on.
  //
  // `size` takes two lengths, a keyword, or `auto` alone — `80mm auto` is invalid and Chromium
  // drops it, which left the receipt on the A4 default (verified with page.pdf). 80 × 297 mm is
  // the roll paper the thermal drivers themselves declare ("Roll Paper 80 x 297 mm"); they cut
  // where the content ends, so the height only bounds one page.
  private pageRule: HTMLStyleElement | null = null;

  ngOnInit(): void {
    const style = document.createElement('style');
    style.id = 'receipt-page-size';
    style.textContent = '@media print { @page { size: 80mm 297mm; margin: 3mm; } }';
    document.head.appendChild(style);
    this.pageRule = style;
  }

  ngOnDestroy(): void {
    this.pageRule?.remove();
    this.pageRule = null;
  }

  print(): void {
    window.print();
  }
}
