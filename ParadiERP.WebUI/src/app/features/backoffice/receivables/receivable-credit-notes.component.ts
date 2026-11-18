import { Component, computed, inject, input, output } from '@angular/core';
import { adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { PrintService } from '../../print/print.service';
import { MENU } from '../../../ui/menu.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { formatDay } from '../backoffice-format';
import { DocRef, withZ } from './receivable-view';
import { ReceivablePagesApi } from './receivable-pages.api';

/**
 * The question this screen answers: **which credit notes has this customer received, against
 * which invoices, and why.**
 *
 * <p>There is no "new" button here on purpose. A credit note corrects one invoice, so it is
 * raised from that invoice's screen where the lines it credits are in front of the reader; the
 * panel note says so. The list is read-only in every sense: nothing on it is edited or deleted,
 * because the credit note is itself the correction.</p>
 */
@Component({
  selector: 'app-receivable-credit-notes',
  standalone: true,
  imports: [ListFooterComponent, ...MENU],
  template: `
    <section class="bo-panel">
      <div class="bo-panel__head">
        <h2 class="bo-panel__title">{{ i18n.t('inv.creditNotes') }}</h2>
        <p class="bo-panel__note">{{ i18n.t('inv.credit.list.note') }}</p>
      </div>

      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <div class="bo-scroll">
        <table class="bo-data" [attr.aria-label]="i18n.t('inv.creditNotes')">
          <thead>
            <tr>
              <th>{{ i18n.t('inv.number') }}</th>
              <th>{{ i18n.t('receipt.date') }}</th>
              <th>{{ i18n.t('inv.credit.onInvoice') }}</th>
              <th>{{ i18n.t('inv.view.reason') }}</th>
              <th class="num">{{ i18n.t('inv.totalHt') }}</th>
              <th class="num">{{ i18n.t('inv.taxTotal') }}</th>
              <th class="num">{{ i18n.t('common.total') }}</th>
              <th>{{ i18n.t('common.actions') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (cn of pager.rows(); track cn.id) {
              <tr>
                <td class="mono" [attr.data-label]="i18n.t('inv.number')">{{ cn.number }}</td>
                <td class="mono" [attr.data-label]="i18n.t('receipt.date')">{{ day(cn.issuedAtUtc) }}</td>
                <td class="mono" [attr.data-label]="i18n.t('inv.credit.onInvoice')">{{ cn.invoiceNumber ?? '—' }}</td>
                <td [attr.data-label]="i18n.t('inv.view.reason')">{{ cn.reason }}</td>
                <td class="num mono" [attr.data-label]="i18n.t('inv.totalHt')">
                  {{ i18n.money(cn.subTotalHt) }}
                </td>
                <td class="num mono" [attr.data-label]="i18n.t('inv.taxTotal')">
                  {{ i18n.money(cn.taxTotal) }}
                </td>
                <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('common.total')">
                  {{ i18n.money(cn.grandTotalTtc) }}
                </td>
                <td class="ui-cell-menu">
                  <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + cn.number">
                    <span menuTrigger aria-hidden="true">⋯</span>
                    @if (cn.invoiceId) {
                      <button type="button" appMenuItem (click)="openInvoice.emit({ id: cn.invoiceId, customerId: cn.customerId })">{{ i18n.t('inv.credit.viewInvoice') }}</button>
                    }
                    <button type="button" appMenuItem (click)="printer.open('credit-note', cn.id)">{{ i18n.t('common.print') }}</button>
                  </app-menu>
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="8" class="bo-empty">
                  @if (!pager.loaded() && !error()) {
                    <strong>{{ i18n.t('common.loading') }}</strong>
                    <span class="bo-loading__rows" aria-hidden="true"></span>
                  } @else {
                    <strong>{{ i18n.t('inv.credit.list.none') }}</strong>
                    <span>{{ i18n.t('inv.credit.list.noneHint') }}</span>
                  }
                </td>
              </tr>
            }
          </tbody>
          @if (pager.total() > 0) {
            <tfoot>
              <tr>
                <th scope="row" colspan="4">{{ i18n.t('common.total') }}</th>
                <td class="num mono">{{ i18n.money(totals().ht) }}</td>
                <td class="num mono">{{ i18n.money(totals().tax) }}</td>
                <td class="num mono bo-cell-strong">{{ i18n.money(totals().ttc) }}</td>
                <td></td>
              </tr>
            </tfoot>
          }
        </table>
      </div>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </section>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class ReceivableCreditNotesComponent {
  private readonly pages = inject(ReceivablePagesApi);
  private readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);
  readonly printer = inject(PrintService);

  readonly customerId = input.required<string>();

  readonly openInvoice = output<DocRef>();

  /**
   * Ten notes to begin with, newest first, a page at a time from the server (owner, 2026-11-17:
   * "paginate in backend, not frontend"). Paged only: the total line is the server's, over every
   * note of the customer, so no filter narrows the rows under it.
   */
  readonly pager = createServerPager(
    (page) => this.pages.creditNotesPage({ skip: page.skip, take: page.take, customerId: page.filters.customerId }),
    { key: 'receivables.creditNotes', filters: () => ({ customerId: this.customerId() }), debounceMs: 0 },
  );

  readonly totals = computed(() => this.pager.summary() ?? { ht: 0, tax: 0, ttc: 0 });

  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  day(value: string | null | undefined): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }
}
