import { Component, computed, input } from '@angular/core';
import { SalesSummary, TopProduct } from '../../core/admin.service';
import { PrintContext } from './print-context';
import { ReportPaper, shareOf } from './print-reports';

/**
 * The sales overview on paper: what was sold over the window, before and after tax, what went
 * back, the tax by rate, the branches and the products that carried it.
 *
 * <p>The screen's share bars become figures — the part of the total each row carried — because
 * this sheet is printed in black and read with a pen. The list of tickets stays on the screen, as
 * it always has: it is a working list with a drill-down, not part of the report.</p>
 */
@Component({
  selector: 'app-print-rep-sales',
  standalone: true,
  template: `
    @if (ctx(); as c) {
      @if (summary(); as s) {
        <dl class="print-figures">
          <div>
            <dt>{{ c.t('reports.grossTtc') }}</dt>
            <dd class="mono">{{ c.money(s.grossTtc) }}</dd>
            <dd class="print-figures__word">{{ c.t('reports.saleCount') }} <span class="mono">{{ s.saleCount }}</span></dd>
          </div>
          <div>
            <dt>{{ c.t('reports.netHt') }}</dt>
            <dd class="mono">{{ c.money(s.netHt) }}</dd>
          </div>
          <div>
            <dt>{{ taxName() }}</dt>
            <dd class="mono">{{ c.money(s.taxTotal) }}</dd>
          </div>
          <div>
            <dt>{{ c.t('reports.refundTotal') }}</dt>
            <dd class="mono">{{ c.money(s.refundTotal) }}</dd>
            <dd class="print-figures__word">{{ c.t('reports.refundCount') }} <span class="mono">{{ s.refundCount }}</span></dd>
          </div>
        </dl>

        @if (s.discountTotal || s.giftCardTotal || s.loyaltyTotal || s.roundingTotal) {
          <table class="print-table print-table--compact">
            <tbody>
              @if (s.discountTotal) {
                <tr>
                  <td>{{ c.t('register.discount') }}</td>
                  <td class="num mono">{{ c.money(s.discountTotal) }}</td>
                </tr>
              }
              @if (s.giftCardTotal) {
                <tr>
                  <td>{{ c.t('reports.giftCards') }}</td>
                  <td class="num mono">{{ c.money(s.giftCardTotal) }}</td>
                </tr>
              }
              @if (s.loyaltyTotal) {
                <tr>
                  <td>{{ c.t('reports.loyalty') }}</td>
                  <td class="num mono">{{ c.money(s.loyaltyTotal) }}</td>
                </tr>
              }
              @if (s.roundingTotal) {
                <tr>
                  <td>{{ c.t('reports.rounding') }}</td>
                  <td class="num mono">{{ c.money(s.roundingTotal) }}</td>
                </tr>
              }
            </tbody>
          </table>
        }

        <table class="print-table print-table--statement">
          <caption>{{ c.t('reports.taxBreakdown') }}</caption>
          <thead>
            <tr>
              <th>{{ c.t('backoffice.products.taxRate') }}</th>
              <th class="num">{{ c.t('receipt.taxBase') }}</th>
              <th class="num">{{ taxName() }}</th>
            </tr>
          </thead>
          <tbody>
            @for (t of s.taxBreakdown; track t.rate) {
              <tr>
                <td>
                  <bdi class="mono" dir="ltr">{{ t.rate }} %</bdi>
                  @if (c.rateLabel(t.rate)) {
                    <span class="print-soft">{{ c.rateLabel(t.rate) }}</span>
                  }
                </td>
                <td class="num mono">{{ c.money(t.baseHt) }}</td>
                <td class="num mono">{{ c.money(t.taxAmount) }}</td>
              </tr>
            } @empty {
              <tr>
                <td colspan="3">{{ c.t('sales.none') }}</td>
              </tr>
            }
          </tbody>
          @if (s.taxBreakdown.length) {
            <tfoot>
              <tr class="print-total">
                <td>{{ c.t('common.total') }}</td>
                <td class="num mono">{{ c.money(taxBase()) }}</td>
                <td class="num mono">{{ c.money(s.taxTotal) }}</td>
              </tr>
            </tfoot>
          }
        </table>

        @if (s.branchBreakdown.length) {
          <table class="print-table print-table--statement">
            <caption>{{ c.t('backoffice.tabs.branches') }}</caption>
            <thead>
              <tr>
                <th>{{ c.t('backoffice.tabs.branches') }}</th>
                <th class="num">{{ c.t('reports.saleCount') }}</th>
                <th class="num">{{ c.t('reports.grossTtc') }}</th>
                <th class="num">{{ c.t('reports.share') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (b of s.branchBreakdown; track b.branchId) {
                <tr>
                  <td>{{ branchName(b.branchId, b.branchName) }}</td>
                  <td class="num mono">{{ b.saleCount }}</td>
                  <td class="num mono">{{ c.money(b.grossTtc) }}</td>
                  <td class="num mono">{{ share(b.grossTtc) }}</td>
                </tr>
              }
            </tbody>
          </table>
        }

        <table class="print-table print-table--statement">
          <caption>{{ c.t('reports.topProducts') }}</caption>
          <thead>
            <tr>
              <th>{{ c.t('receipt.item') }}</th>
              <th class="num">{{ c.t('register.qty') }}</th>
              <th class="num">{{ c.t('reports.grossTtc') }}</th>
              <th class="num">{{ c.t('reports.share') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (p of s.topProducts; track p.productId) {
              <tr>
                <td>{{ productName(p) }}</td>
                <td class="num mono">{{ c.qty(p.qty) }}</td>
                <td class="num mono">{{ c.money(p.revenue) }}</td>
                <td class="num mono">{{ share(p.revenue) }}</td>
              </tr>
            } @empty {
              <tr>
                <td colspan="4">{{ c.t('reports.emptyTitle') }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    }
  `,
})
export class PrintRepSalesComponent {
  readonly ctx = input.required<PrintContext>();
  readonly report = input.required<ReportPaper>();

  readonly summary = computed<SalesSummary | null>(() => {
    const r = this.report();
    return r.name === 'sales' ? r.summary : null;
  });

  private readonly products = computed(() => {
    const r = this.report();
    return new Map((r.name === 'sales' ? r.products : []).map((p) => [p.id, p]));
  });

  readonly taxName = computed(() => this.ctx().text(this.ctx().regional.taxNames));
  readonly taxBase = computed(() => (this.summary()?.taxBreakdown ?? []).reduce((sum, t) => sum + t.baseHt, 0));

  share(value: number): string {
    return shareOf(value, this.summary()?.grossTtc ?? 0);
  }

  /** The product in the document language when the catalogue knows it; the report's own name otherwise. */
  productName(p: TopProduct): string {
    const product = this.products().get(p.productId);
    return product ? this.ctx().name(product) || p.name : p.name;
  }

  branchName(id: string, fallback: string): string {
    const branch = this.ctx().branches.find((b) => b.id === id);
    return branch ? this.ctx().name(branch) : fallback;
  }
}
