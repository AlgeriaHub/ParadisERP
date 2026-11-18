import { Component, computed, input } from '@angular/core';
import { formatDay, localizedName } from '../backoffice/backoffice-format';
import { QuantityStatement, QuantityStatementRate, QuantityStatementWarning } from '../backoffice/rentals/rentals-api.service';
import { PrintDocumentComponent } from './print-document.component';
import { LabelledLine, PrintContext, partyLines } from './print-context';

/**
 * The customer quantity statement on A4 (كشف كميات العميل, owner 2026-10-12): the same figures as
 * the screen — summary, one row per item with its movements under it, then each contract — for
 * the same query. No payment or amount owed appears on it.
 */
@Component({
  selector: 'app-print-quantity-statement',
  standalone: true,
  imports: [PrintDocumentComponent],
  template: `
    @if (ctx(); as c) {
      @for (copy of copyIndexes(); track copy) {
        <app-print-sheet
          [ctx]="c"
          [kind]="null"
          [title]="c.t('qstmt.title')"
          titleKey="qstmt.title"
          [number]="statement().customerCode"
          [meta]="meta()"
          [breakBefore]="copy > 0"
        >
          <div class="print-parties">
            <div class="print-block">
              <span class="print-block__title">{{ c.t('qstmt.customer') }}</span>
              @for (line of party(); track $index) {
                <div [class.print-line--lead]="!line.label">
                  @if (line.label) {
                    <span class="print-line__label">{{ line.label }}</span>
                  }
                  <span [class.mono]="!!line.label" [attr.dir]="line.ltr ? 'ltr' : null">{{ line.value }}</span>
                </div>
              }
            </div>
            <div class="print-block">
              <span class="print-block__title">{{ period() }}</span>
              <div>
                <span class="print-line__label">{{ c.t('qstmt.sum.itemTypes') }}</span>
                <span class="mono">{{ s().summary.itemTypes }}</span>
              </div>
              @if (!s().current) {
                <div>
                  <span class="print-line__label">{{ c.t('qstmt.sum.issued') }}</span>
                  <span class="mono">{{ s().summary.issued }}</span>
                </div>
                <div>
                  <span class="print-line__label">{{ c.t('qstmt.sum.returned') }}</span>
                  <span class="mono">{{ s().summary.returned }}</span>
                </div>
                <div>
                  <span class="print-line__label">{{ c.t('qstmt.sum.missingDamaged') }}</span>
                  <span class="mono">{{ s().summary.missingDamaged }}</span>
                </div>
              }
              <div class="print-line--lead">
                <span class="print-line__label">{{ c.t('qstmt.sum.withCustomer') }}</span>
                <span class="mono">{{ s().summary.withCustomer }}</span>
              </div>
              @if (!s().current) {
                <div class="print-line--lead">
                  <span class="print-line__label">{{ c.t('qstmt.sum.value') }}</span>
                  <span class="mono">{{ c.money(s().summary.value) }}</span>
                  @if (s().summary.provisionalValue > 0) {
                    <span class="print-soft"> {{ c.t('qstmt.provisional') }}</span>
                  }
                </div>
              }
            </div>
          </div>

          <p class="print-sentence">
            {{ s().firstIssueOn ? c.t('qstmt.notRecorded', { date: day(s().firstIssueOn!) }) : c.t('qstmt.noIssue') }}
          </p>
          @for (w of s().warnings; track $index) {
            <p class="print-sentence">{{ warning(w) }}</p>
          }
          @if (!s().current && s().summary.provisionalValue > 0) {
            <p class="print-sentence">{{ c.t('qstmt.provisionalNote') }}</p>
          }

          <table class="print-table">
            <caption>{{ c.t('qstmt.itemsTitle') }}</caption>
            <thead>
              <tr>
                <th>{{ c.t('qstmt.col.sku') }}</th>
                <th>{{ c.t('qstmt.col.item') }}</th>
                @if (!s().current) {
                  <th class="num">{{ c.t('qstmt.col.opening') }}</th>
                  <th class="num">{{ c.t('qstmt.col.issued') }}</th>
                  <th class="num">{{ c.t('qstmt.col.returned') }}</th>
                  <th class="num">{{ c.t('qstmt.col.missingDamaged') }}</th>
                  @if (hasTransfers()) {
                    <th class="num">{{ c.t('qstmt.col.transfers') }}</th>
                  }
                }
                <th class="num">{{ c.t('qstmt.col.remaining') }}</th>
                @if (!s().current) {
                  <th class="num">{{ c.t('qstmt.col.rate') }}</th>
                  <th class="num">{{ c.t('qstmt.col.value') }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (item of s().items; track item.productId) {
                <tr>
                  <td class="mono">{{ item.sku }}</td>
                  <td>
                    {{ name(item) }}
                    @if (item.negativeBalance) {
                      <span class="print-soft"> {{ c.t('qstmt.negative') }}</span>
                    }
                  </td>
                  @if (!s().current) {
                    <td class="num mono">{{ item.opening }}</td>
                    <td class="num mono">{{ item.issued }}</td>
                    <td class="num mono">{{ item.returned }}</td>
                    <td class="num mono">{{ item.missingDamaged }}</td>
                    @if (hasTransfers()) {
                      <td class="num mono">{{ item.transferNet }}</td>
                    }
                  }
                  <td class="num mono">{{ item.remaining }}</td>
                  @if (!s().current) {
                    <td class="num">{{ rates(item.noRent, item.rates) }}</td>
                    <td class="num mono">{{ c.money(item.value) }}</td>
                  }
                </tr>
                @if (!s().current) {
                  @for (m of item.movements; track $index) {
                    <tr class="print-soft">
                      <td class="mono">{{ day(m.on) }}</td>
                      <td [attr.colspan]="movementSpan()">
                        {{ c.t('qstmt.move.' + m.kind) }}
                        @if (m.documentNumber) {
                          <span class="mono"> {{ m.documentNumber }}</span>
                        }
                        @if (m.contractNumber) {
                          <span> · {{ c.t('qstmt.col.contract') }} <span class="mono">{{ m.contractNumber }}</span></span>
                        }
                        @if (m.branchId) {
                          <span> · {{ branchName(m.branchId) }}</span>
                        }
                        @if (m.siteName) {
                          <span> · {{ m.siteName }}</span>
                        }
                      </td>
                      <td class="num mono">{{ m.kind === 'opening' ? '' : signed(m.qty) }}</td>
                      <td class="num mono">{{ c.t('qstmt.col.balance') }} {{ m.balance }}</td>
                      <td class="num mono">{{ m.unitPrice !== null ? c.money(m.unitPrice) : '' }}</td>
                      <td></td>
                    </tr>
                  }
                }
              } @empty {
                <tr>
                  <td [attr.colspan]="itemColumns()">{{ c.t('qstmt.nothing') }}</td>
                </tr>
              }
            </tbody>
            @if (!s().current) {
              <tfoot>
                <tr class="print-total">
                  <td colspan="2">{{ c.t('qstmt.total') }}</td>
                  <td class="num mono">{{ openingTotal() }}</td>
                  <td class="num mono">{{ s().summary.issued }}</td>
                  <td class="num mono">{{ s().summary.returned }}</td>
                  <td class="num mono">{{ s().summary.missingDamaged }}</td>
                  @if (hasTransfers()) {
                    <td></td>
                  }
                  <td class="num mono">{{ s().summary.withCustomer }}</td>
                  <td></td>
                  <td class="num mono">{{ c.money(s().summary.value) }}</td>
                </tr>
              </tfoot>
            }
          </table>

          @if (s().contracts.length > 0) {
            <table class="print-table">
              <caption>{{ c.t('qstmt.contractsTitle') }}</caption>
              <thead>
                <tr>
                  <th>{{ c.t('qstmt.col.contract') }}</th>
                  <th>{{ c.t('qstmt.col.issueDate') }}</th>
                  <th>{{ c.t('qstmt.col.item') }}</th>
                  @if (!s().current) {
                    <th class="num">{{ c.t('qstmt.col.issued') }}</th>
                    <th class="num">{{ c.t('qstmt.col.returned') }}</th>
                    <th class="num">{{ c.t('qstmt.col.missingDamaged') }}</th>
                  }
                  <th class="num">{{ c.t('qstmt.col.remaining') }}</th>
                  @if (!s().current) {
                    <th class="num">{{ c.t('qstmt.col.rate') }}</th>
                    <th class="num">{{ c.t('qstmt.col.value') }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (k of s().contracts; track k.contractId) {
                  @for (l of k.lines; track l.lineId; let first = $first) {
                    <tr>
                      <td>
                        @if (first) {
                          <span class="mono">{{ k.number }}</span>
                          <div class="print-soft">
                            {{ c.t('qstmt.status.' + k.status) }} · {{ branchName(k.branchId) }}{{ k.siteName ? ' · ' + k.siteName : '' }}
                          </div>
                        }
                      </td>
                      <td class="mono">{{ l.firstIssueOn ? day(l.firstIssueOn) : '' }}</td>
                      <td>
                        <span class="mono">{{ l.sku }}</span> {{ name(l) }}
                        @if (l.transferredIn || l.transferredOut) {
                          <div class="print-soft">
                            {{ c.t('qstmt.col.transferIn') }} {{ l.transferredIn }} · {{ c.t('qstmt.col.transferOut') }} {{ l.transferredOut }}
                          </div>
                        }
                      </td>
                      @if (!s().current) {
                        <td class="num mono">{{ l.issued }}</td>
                        <td class="num mono">{{ l.returned }}</td>
                        <td class="num mono">{{ l.missingDamaged }}</td>
                      }
                      <td class="num mono">{{ l.remaining }}</td>
                      @if (!s().current) {
                        <td class="num">{{ rates(l.noRent, l.rates) }}</td>
                        <td class="num mono">{{ c.money(l.value) }}</td>
                      }
                    </tr>
                  }
                }
              </tbody>
            </table>
          }
        </app-print-sheet>
      }
    }
  `,
})
export class PrintQuantityStatementComponent {
  readonly ctx = input.required<PrintContext>();
  readonly statement = input.required<QuantityStatement>();
  readonly copies = input(1);

  readonly s = computed(() => this.statement());
  readonly copyIndexes = computed(() => Array.from({ length: Math.max(1, this.copies()) }, (_, i) => i));
  readonly customer = computed(() => this.ctx().customers.find((x) => x.id === this.statement().customerId) ?? null);
  readonly party = computed(() => partyLines(this.ctx(), this.customer(), { name: this.statement().customerName }));
  readonly hasTransfers = computed(() => this.statement().items.some((i) => i.transferNet !== 0));
  readonly itemColumns = computed(() => (this.statement().current ? 3 : 9 + (this.hasTransfers() ? 1 : 0)));
  /** A movement row: date, then the words across the middle, then quantity, balance, rate and an empty value cell. */
  readonly movementSpan = computed(() => this.itemColumns() - 5);
  readonly openingTotal = computed(() => this.statement().items.reduce((sum, i) => sum + i.opening, 0));

  readonly period = computed(() => {
    const c = this.ctx();
    const s = this.statement();
    return s.current ? c.t('qstmt.asOf', { date: this.day(s.to) }) : c.t('qstmt.period', { from: this.day(s.from), to: this.day(s.to) });
  });

  readonly meta = computed<LabelledLine[]>(() => [{ label: this.ctx().t('qstmt.title'), value: this.period() }]);

  /** A plain `yyyy-MM-dd` day, printed as that day whatever the zone. */
  day(value: string): string {
    return formatDay(`${value.slice(0, 10)}T00:00:00Z`, this.ctx().lang, 'UTC');
  }

  name(row: { name: string; nameAr: string | null }): string {
    return this.ctx().lang === 'ar' && row.nameAr ? row.nameAr : row.name;
  }

  branchName(id: string): string {
    const branch = this.ctx().branches.find((b) => b.id === id);
    return branch ? localizedName(branch, this.ctx().lang) : '—';
  }

  rates(noRent: boolean, rates: QuantityStatementRate[]): string {
    const c = this.ctx();
    if (noRent) return c.t('qstmt.noRent');
    return rates.map((r) => (rates.length > 1 ? `${c.t('qstmt.rate.' + r.kind)} ` : '') + c.money(r.unitPrice)).join(' · ');
  }

  signed(qty: number): string {
    return qty > 0 ? `+${qty}` : String(qty);
  }

  warning(w: QuantityStatementWarning): string {
    return this.ctx().t('qstmt.warn.' + w.code, {
      date: w.on ? this.day(w.on) : '',
      contract: w.contractNumber ?? '',
      qty: w.qty ?? 0,
    });
  }
}
