import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';

/**
 * Manufacturing › Material flow (costing, Phase A — R3 of docs/manufacturing/costing-plan.md): the
 * manufacturing documents against stock on the lines the reader reaches. Every issue, return and release
 * names the stock movement it made with the same quantity; every order material's counters match its
 * documents; every line stock equals the sum of its movements. Anything that does not is listed, in words.
 */
@Component({
  selector: 'app-mfg-material-flow',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ListFooterComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="mf-kind">{{ i18n.t('mfg.flow.check') }}</label>
          <select id="mf-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (k of kinds(); track k) {
              <option [ngValue]="k">{{ i18n.t('mfg.flow.kind.' + k) }}</option>
            }
          </select>
        </div>
        <div class="ui-toolbar__end">
          @if (kindFilter() !== 'all') {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() && data()?.issues) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (data(); as d) {
        <div class="ui-metrics">
          <div class="ui-metric ui-metric--lead">
            <span class="ui-metric__label">{{ i18n.t('mfg.flow.issues') }}</span>
            <strong class="ui-metric__value mono">{{ d.issues }}</strong>
          </div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.flow.movementsChecked') }}</span><strong class="ui-metric__value mono">{{ d.movementsChecked }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.flow.materialsChecked') }}</span><strong class="ui-metric__value mono">{{ d.materialsChecked }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.flow.stocksChecked') }}</span><strong class="ui-metric__value mono">{{ d.stocksChecked }}</strong></div>
        </div>
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !data()?.issues) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!data()) {
          <app-table-state kind="loading" />
        } @else if (!data()?.issues) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.flow.allMatch')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.flow.check') }}</th>
                  <th>{{ i18n.t('mfg.reports.document') }}</th>
                  <th>{{ i18n.t('mfg.boms.product') }}</th>
                  <th>{{ i18n.t('mfg.flow.location') }}</th>
                  <th class="num">{{ i18n.t('mfg.flow.expected') }}</th>
                  <th class="num">{{ i18n.t('mfg.flow.actual') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (i of pager.rows(); track $index) {
                  <tr>
                    <td [attr.data-label]="i18n.t('mfg.flow.check')">{{ i18n.t('mfg.flow.kind.' + i.kind) }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.reports.document')">
                      <span dir="ltr">{{ i.reference || '—' }}</span>
                      @if (i.orderNumber && i.orderNumber !== i.reference) {
                        <span class="ui-sub" dir="ltr">{{ i.orderNumber }}</span>
                      }
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.product')"><span dir="ltr">{{ i.sku || '—' }}</span></td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.flow.location')">{{ i.locationCode || '—' }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.flow.expected')">{{ i.expected === null ? '—' : qty(i.expected) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.flow.actual')">{{ i.actual === null ? '—' : qty(i.actual) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MfgMaterialFlowComponent implements OnInit {
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly qty = qty;

  /** Which check found the difference, filtered on the server. */
  readonly kindFilter = signal<string>('all');
  /**
   * The differences a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend").
   * The summary is the report's tiles, over every difference, and the checks it names.
   */
  readonly pager = createServerPager(
    (page) => this.pages.materialFlow({ skip: page.skip, take: page.take }, page.filters),
    { key: 'mfg.materialFlow', filters: () => ({ kind: this.kindFilter() }) },
  );
  /** The report's tiles; null until the first answer. */
  readonly data = computed(() => this.pager.summary());
  /** The checks the report names, in the order they first appear. */
  readonly kinds = computed(() => this.data()?.kinds ?? []);

  /** The report could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.kindFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.flow.lead'));
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }
}
