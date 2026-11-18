import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay } from '../backoffice-format';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { BomStatus, BomSummary, ManufacturingApiService, ProductionLineSummary } from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';

/**
 * Manufacturing › Recipes (Phase 1 §4): every version of every bill of materials the reader reaches —
 * the company's recipes for every line, and those of the lines they are on. One version per product
 * and line is active; drafts are being written; retired ones stay, frozen, because what was made from
 * them names them. The product's name opens the version.
 */
@Component({
  selector: 'app-mfg-boms',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    @if (canWrite()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="create.emit(lineFilter())"><span aria-hidden="true">+</span> {{ i18n.t('mfg.boms.new') }}</button>
      </ng-template>
    }
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter ui-filter--search">
          <label for="bm-q">{{ i18n.t('products.search') }}</label>
          <input id="bm-q" type="search" [placeholder]="i18n.t('products.searchHint')" [ngModel]="query()" (ngModelChange)="query.set($event)" />
        </div>
        <div class="ui-filter">
          <label for="bm-line">{{ i18n.t('mfg.boms.line') }}</label>
          <select id="bm-line" [ngModel]="lineFilter()" (ngModelChange)="lineFilterChange.emit($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.boms.everyLine') }}</option>
            @for (l of lines(); track l.id) {
              <option [ngValue]="l.id">{{ l.code }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="bm-status">{{ i18n.t('common.state') }}</label>
          <select id="bm-status" [ngModel]="status()" (ngModelChange)="status.set($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.boms.allStates') }}</option>
            @for (s of statuses; track s) {
              <option [ngValue]="s">{{ i18n.t('mfg.bomStatus.' + s) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="bm-scope">{{ i18n.t('mfg.filter.scope') }}</label>
          <select id="bm-scope" [ngModel]="scope()" (ngModelChange)="scope.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="every">{{ i18n.t('mfg.boms.everyLine') }}</option>
            <option ngValue="one">{{ i18n.t('mfg.filter.lineOwn') }}</option>
          </select>
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() && pager.loaded()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !pager.loaded()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.boms.none')">
            @if (canWrite()) {
              <button type="button" class="btn-primary" (click)="create.emit(lineFilter())">{{ i18n.t('mfg.boms.new') }}</button>
            }
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.boms.product') }}</th>
                  <th>{{ i18n.t('mfg.boms.line') }}</th>
                  <th class="num">{{ i18n.t('mfg.boms.version') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="num">{{ i18n.t('mfg.boms.batch') }}</th>
                  <th class="num">{{ i18n.t('mfg.boms.components') }}</th>
                  <th>{{ i18n.t('mfg.boms.activatedOn') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (b of pager.rows(); track b.id) {
                  <tr [class.is-muted]="b.status === 'Retired'">
                    <td [attr.data-label]="i18n.t('mfg.boms.product')">
                      <button type="button" class="ui-link" (click)="open.emit(b.id)">{{ productName(b) }}</button>
                      <span class="ui-sub mono" dir="ltr">{{ b.productSku }}</span>
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.line')">{{ b.productionLineCode || i18n.t('mfg.boms.everyLine') }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.boms.version')">v{{ b.version }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(b.status)">{{ i18n.t('mfg.bomStatus.' + b.status) }}</app-status>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.boms.batch')"><span dir="ltr">{{ qty(b.batchQuantity) }} {{ b.batchUnitCode }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.boms.components')">{{ b.lineCount }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.activatedOn')">{{ b.activatedAtUtc ? day(b.activatedAtUtc) : '—' }}</td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + b.productSku + ' v' + b.version">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="open.emit(b.id)">{{ i18n.t('mfg.boms.open') }}</button>
                      </app-menu>
                    </td>
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
export class MfgBomsComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly regional = inject(RegionalService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** The line in the address (`?line=`): the filter, kept in the address so Back returns to it. */
  readonly lineFilter = input<string | null>(null);
  /** Whether the reader writes recipes (`manufacturing.bom.manage`). The server still decides per line. */
  readonly canWrite = input(false);
  readonly open = output<string>();
  readonly create = output<string | null>();
  readonly lineFilterChange = output<string | null>();

  readonly statuses: BomStatus[] = ['Draft', 'Active', 'Retired'];
  readonly qty = qty;
  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly query = signal('');
  readonly status = signal<BomStatus | null>(null);
  /** A recipe for every line, or one line's own. */
  readonly scope = signal<'all' | 'every' | 'one'>('all');

  readonly filtered = computed(() => !!this.query().trim() || !!this.status() || this.scope() !== 'all');

  /**
   * The recipes a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"):
   * the line in the address, the state, the scope and the search run there over every version the reader
   * reaches; a change goes back to page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.boms({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.boms',
      filters: () => ({ lineId: this.lineFilter(), status: this.status(), q: this.query().trim(), scope: this.scope() }),
    },
  );

  /** The list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.query.set('');
    this.status.set(null);
    this.scope.set('all');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.boms.lead'));
    this.api.lines(true).then((l) => this.lines.set(l), () => undefined);
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  productName(b: BomSummary): string {
    const lang = this.i18n.lang();
    return (lang === 'ar' ? b.productNameAr : lang === 'en' ? b.productNameEn : b.productNameFr) || b.productNameFr || b.productSku;
  }

  day(value: string): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  tone(status: BomStatus) {
    return toneOf('bom', status);
  }
}
