import { Component, computed, input } from '@angular/core';
import { formatQty } from '../backoffice/backoffice-format';
import { ItemLocationRow, ItemStatusDetail, ItemStatusReport } from '../backoffice/item-status/item-status-api.service';
import { PrintContext } from './print-context';
import { ReportPaper } from './print-reports';

/**
 * Item Movement & Status report on paper (print-reports.ts):
 * - The summary table with its totals row
 * - The scope sentence and the instantaneous transfer sentence
 * - When detail is present: locations breakdown, rented table, and movement history
 */
@Component({
  selector: 'app-print-rep-item-status',
  standalone: true,
  template: `
    @if (ctx(); as c) {
      @if (itemReport(); as r) {
        <p class="print-sentence print-sentence--strong">{{ scopeSentence() }}</p>
        <p class="print-sentence print-soft">{{ c.t('print.rep.itemStatus.transitNote') }}</p>

        <table class="print-table print-table--statement">
          <thead>
            <tr>
              <th>{{ c.t('itemStatus.col.sku') }}</th>
              <th>{{ c.t('itemStatus.col.item') }}</th>
              <th class="num">{{ c.t('itemStatus.col.total') }}</th>
              <th class="num">{{ c.t('itemStatus.col.warehouses') }}</th>
              <th class="num">{{ c.t('itemStatus.col.branches') }}</th>
              <th class="num">{{ c.t('itemStatus.col.available') }}</th>
              <th class="num">{{ c.t('itemStatus.col.rented') }}</th>
              <th class="num">{{ c.t('itemStatus.col.unavailable') }}</th>
              <th class="num">{{ c.t('itemStatus.col.inTransit') }}</th>
              @if (r.scope.othersAvailableShown) {
                <th class="num">{{ c.t('itemStatus.col.otherAvailable') }}</th>
              }
            </tr>
          </thead>
          <tbody>
            @for (row of r.rows; track row.productId) {
              <tr>
                <td class="mono">{{ row.sku }}</td>
                <td>{{ itemName(row) }}</td>
                <td class="num mono">{{ fmtQty(row.total) }}</td>
                <td class="num mono">{{ fmtQty(row.warehouses) }}</td>
                <td class="num mono">{{ fmtQty(row.branches) }}</td>
                <td class="num mono">{{ fmtQty(row.available) }}</td>
                <td class="num mono">
                  {{ fmtQty(row.rented) }}
                  @if (row.unexplainedOnRent) {
                    <span class="print-soft">({{ c.t('itemStatus.unexplainedWarning', { n: fmtQty(row.unexplainedOnRent) }) }})</span>
                  }
                </td>
                <td class="num mono">
                  {{ fmtQty(row.maintenance + row.scrap) }}
                  @if (row.maintenance || row.scrap) {
                    <span class="print-soft">({{ c.t('itemStatus.unavailableSplit', { m: fmtQty(row.maintenance), s: fmtQty(row.scrap) }) }})</span>
                  }
                </td>
                <td class="num mono">{{ fmtQty(row.inTransit) }}</td>
                @if (r.scope.othersAvailableShown) {
                  <td class="num mono">{{ fmtQty(row.otherAvailable) }}</td>
                }
              </tr>
            } @empty {
              <tr>
                <td [attr.colspan]="r.scope.othersAvailableShown ? 10 : 9">{{ c.t('common.nothingHere') }}</td>
              </tr>
            }
          </tbody>
          @if (r.totals; as t) {
            <tfoot>
              <tr class="print-total">
                <td colspan="2">{{ c.t('common.total') }}</td>
                <td class="num mono">{{ fmtQty(t.total) }}</td>
                <td class="num mono">{{ fmtQty(t.warehouses) }}</td>
                <td class="num mono">{{ fmtQty(t.branches) }}</td>
                <td class="num mono">{{ fmtQty(t.available) }}</td>
                <td class="num mono">{{ fmtQty(t.rented) }}</td>
                <td class="num mono">{{ fmtQty(t.maintenance + t.scrap) }}</td>
                <td class="num mono">{{ fmtQty(t.inTransit) }}</td>
                @if (r.scope.othersAvailableShown) {
                  <td class="num mono">{{ fmtQty(t.otherAvailable) }}</td>
                }
              </tr>
            </tfoot>
          }
        </table>

        @if (detail(); as d) {
          <div class="print-notice">
            <strong>{{ itemName(d.item) }} <span class="mono print-soft">({{ d.item.sku }})</span></strong>
            @if (d.item.unexplainedOnRent) {
              <p class="print-soft">
                {{ c.t('itemStatus.detail.reconciliationWarn', { rented: fmtQty(d.item.rented), stillOut: fmtQty(d.item.stillOut), diff: fmtQty(d.item.unexplainedOnRent) }) }}
              </p>
            }
          </div>

          @if (d.locations.length) {
            <table class="print-table print-table--statement">
              <caption>{{ c.t('itemStatus.detail.locations.title') }}</caption>
              <thead>
                <tr>
                  <th>{{ c.t('itemStatus.detail.locations.location') }}</th>
                  <th>{{ c.t('itemStatus.filter.type') }}</th>
                  <th class="num">{{ c.t('itemStatus.col.total') }}</th>
                  <th class="num">{{ c.t('itemStatus.col.available') }}</th>
                  <th class="num">{{ c.t('itemStatus.col.rented') }}</th>
                  <th class="num">{{ c.t('itemStatus.detail.locations.contractsStillOut') }}</th>
                  <th class="num">{{ c.t('itemStatus.detail.locations.unexplained') }}</th>
                  <th class="num">{{ c.t('itemStatus.status.maintenance') }}</th>
                  <th class="num">{{ c.t('itemStatus.status.scrap') }}</th>
                  <th class="num">{{ c.t('itemStatus.col.inTransit') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (loc of d.locations; track loc.branchId) {
                  <tr>
                    <td>{{ loc.code }} — {{ itemName(loc) }}</td>
                    <td>{{ c.t(loc.isWarehouse ? 'itemStatus.detail.locations.warehouse' : 'itemStatus.detail.locations.branch') }}</td>
                    <td class="num mono">{{ fmtQty(loc.total) }}</td>
                    <td class="num mono">{{ fmtQty(loc.available) }}</td>
                    <td class="num mono">{{ fmtQty(loc.onRent) }}</td>
                    <td class="num mono">{{ fmtQty(loc.stillOut) }}</td>
                    <td class="num mono">{{ fmtQty(loc.unexplainedOnRent) }}</td>
                    <td class="num mono">{{ fmtQty(loc.maintenance) }}</td>
                    <td class="num mono">{{ fmtQty(loc.scrap) }}</td>
                    <td class="num mono">{{ fmtQty(loc.inTransit) }}</td>
                  </tr>
                }
              </tbody>
            </table>
            @if (hasAvailableOnly(d.locations)) {
              <p class="print-sentence print-soft">{{ c.t('itemStatus.detail.locations.availableOnlyCaption') }}</p>
            }
          }

          @if (d.rented.length) {
            <table class="print-table print-table--statement">
              <caption>{{ c.t('itemStatus.detail.rented.title') }}</caption>
              <thead>
                <tr>
                  <th>{{ c.t('itemStatus.detail.rented.customer') }}</th>
                  <th>{{ c.t('itemStatus.detail.rented.contract') }}</th>
                  <th>{{ c.t('itemStatus.detail.rented.issueBranch') }}</th>
                  <th>{{ c.t('itemStatus.detail.rented.site') }}</th>
                  <th>{{ c.t('itemStatus.detail.rented.issueNote') }}</th>
                  <th>{{ c.t('itemStatus.detail.rented.issuedOn') }}</th>
                  <th>{{ c.t('itemStatus.detail.rented.expectedReturn') }}</th>
                  <th>{{ c.t('itemStatus.detail.rented.state') }}</th>
                  <th class="num">{{ c.t('itemStatus.detail.rented.qty') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (rent of d.rented; track rent.contractId + '-' + rent.issueNoteId) {
                  <tr>
                    <td>{{ rent.customerName }} <span class="mono print-soft">({{ rent.customerCode }})</span></td>
                    <td class="mono">{{ rent.contractNumber }}</td>
                    <td>{{ branchName(rent.issueBranchId) }}</td>
                    <td>{{ rent.siteName || '—' }}</td>
                    <td class="mono">{{ rent.issueNoteNumber }}</td>
                    <td class="mono">{{ c.day(rent.issuedOn) }}</td>
                    <td class="mono">{{ c.day(rent.expectedReturnOn) }}</td>
                    <td>{{ c.t(rent.overdue ? 'itemStatus.detail.rented.overdue' : 'itemStatus.detail.rented.onTime') }}</td>
                    <td class="num mono">{{ fmtQty(rent.qty) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }

          @if (d.movements.rows.length) {
            <table class="print-table print-table--statement">
              <caption>{{ c.t('itemStatus.detail.movements.title') }}</caption>
              <thead>
                <tr>
                  <th>{{ c.t('itemStatus.detail.movements.date') }}</th>
                  <th>{{ c.t('itemStatus.detail.movements.type') }}</th>
                  <th>{{ c.t('itemStatus.detail.movements.fromCol') }}</th>
                  <th>{{ c.t('itemStatus.detail.movements.toCol') }}</th>
                  <th>{{ c.t('itemStatus.detail.movements.document') }}</th>
                  <th>{{ c.t('itemStatus.detail.movements.customer') }}</th>
                  <th class="num">{{ c.t('itemStatus.detail.movements.in') }}</th>
                  <th class="num">{{ c.t('itemStatus.detail.movements.out') }}</th>
                  <th class="num">{{ c.t('itemStatus.detail.movements.balance') }}</th>
                  <th>{{ c.t('itemStatus.detail.movements.user') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td colspan="8">{{ c.t('itemStatus.detail.movements.opening') }}</td>
                  <td class="num mono">{{ fmtQty(d.movements.opening) }}</td>
                  <td></td>
                </tr>
                @for (m of d.movements.rows; track $index) {
                  <tr>
                    <td class="mono">{{ c.dateTime(m.atUtc) }}</td>
                    <td>
                      {{ mvtLabel(m.type) }}
                      @if (m.fromState || m.toState) {
                        <span class="print-soft">({{ stateWord(m.fromState) }} → {{ stateWord(m.toState) }})</span>
                      }
                      @if (m.detail) {
                        <span class="print-soft">({{ m.detail }})</span>
                      }
                    </td>
                    <td>{{ m.fromBranchId ? branchName(m.fromBranchId) : '—' }}</td>
                    <td>{{ m.toBranchId ? branchName(m.toBranchId) : '—' }}</td>
                    <td class="mono">{{ m.documentNumber || '—' }}</td>
                    <td>{{ m.customerName || m.supplierName || '—' }}</td>
                    <td class="num mono">{{ m.in ? fmtQty(m.in) : '' }}</td>
                    <td class="num mono">{{ m.out ? fmtQty(m.out) : '' }}</td>
                    <td class="num mono">{{ fmtQty(m.balance) }}</td>
                    <td>{{ m.userName || '—' }}</td>
                  </tr>
                }
                <tr>
                  <td colspan="8"><strong>{{ c.t('itemStatus.detail.movements.closing') }}</strong></td>
                  <td class="num mono"><strong>{{ fmtQty(d.movements.closing) }}</strong></td>
                  <td></td>
                </tr>
              </tbody>
            </table>

            @if (d.movements.truncated) {
              <p class="print-sentence print-soft">{{ c.t('itemStatus.detail.movements.truncated') }}</p>
            }
            @if (d.movements.unrecordedDifference) {
              <p class="print-notice">{{ c.t('itemStatus.detail.movements.unrecordedDiff', { n: fmtQty(d.movements.unrecordedDifference) }) }}</p>
            }
          }
        }
      }
    }
  `,
})
export class PrintRepItemStatusComponent {
  readonly ctx = input.required<PrintContext>();
  readonly report = input.required<ReportPaper>();

  readonly itemReport = computed<ItemStatusReport | null>(() => {
    const r = this.report();
    return r.name === 'item-status' ? r.report : null;
  });

  readonly detail = computed<ItemStatusDetail | null>(() => {
    const r = this.report();
    return r.name === 'item-status' ? r.detail : null;
  });

  scopeSentence(): string {
    const rep = this.itemReport();
    if (!rep) return '';
    const c = this.ctx();
    const scope = rep.scope;
    if (scope.kind === 'company') return c.t('itemStatus.scope.company');
    if (scope.kind === 'location') {
      const loc = c.branches.find((b) => b.id === scope.locationId);
      return loc ? c.name(loc) : (scope.locationId ?? '—');
    }
    let s = c.t('itemStatus.scope.own');
    if (scope.othersAvailableShown) s += c.t('itemStatus.scope.andOthers');
    return s;
  }

  itemName(row: { nameAr: string; nameFr: string; nameEn: string }): string {
    return this.ctx().name(row);
  }

  branchName(id: string): string {
    const b = this.ctx().branches.find((br) => br.id === id);
    return b ? this.ctx().name(b) : id;
  }

  fmtQty(v: number | null | undefined): string {
    return formatQty(v, this.ctx().lang);
  }

  mvtLabel(type: string): string {
    const key = `itemStatus.mvt.${type}`;
    const t = this.ctx().t(key);
    return t === key ? type : t;
  }

  stateWord(state: string | null | undefined): string {
    if (!state) return '';
    const key = `itemStatus.stateWord.${state.toLowerCase()}`;
    const t = this.ctx().t(key);
    return t === key ? state : t;
  }

  hasAvailableOnly(locations: ItemLocationRow[]): boolean {
    return locations.some((l) => l.availableOnly);
  }
}
