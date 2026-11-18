import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, ProductionLineSummary } from './manufacturing-api.service';
import { LineEditorComponent } from './line-editor.component';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError } from './manufacturing-shared';

/**
 * Manufacturing › Production lines (Phase 1 §1): the lines this reader reaches — every line with
 * company scope, otherwise the lines they are on and the line of their own branch. Each stands on a
 * warehouse of its own, which holds its stock. The line's name opens it.
 */
@Component({
  selector: 'app-mfg-lines',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, LineEditorComponent, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    @if (mayCreate()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="editor.open(null)"><span aria-hidden="true">+</span> {{ i18n.t('mfg.lines.new') }}</button>
      </ng-template>
    }
    <ng-template appPageToolbar>
      <div class="ui-toolbar">
        <div class="ui-filter">
          <label for="ln-state">{{ i18n.t('common.state') }}</label>
          <select id="ln-state" [ngModel]="includeInactive()" (ngModelChange)="includeInactive.set($event)">
            <option [ngValue]="false">{{ i18n.t('common.active') }}</option>
            <option [ngValue]="true">{{ i18n.t('mfg.lines.allStates') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="ln-manager">{{ i18n.t('mfg.filter.hasManager') }}</label>
          <select id="ln-manager" [ngModel]="managerFilter()" (ngModelChange)="managerFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
            <option ngValue="no">{{ i18n.t('filters.no') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="ln-boms">{{ i18n.t('mfg.filter.hasActiveBom') }}</label>
          <select id="ln-boms" [ngModel]="bomFilter()" (ngModelChange)="bomFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
            <option ngValue="no">{{ i18n.t('filters.no') }}</option>
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
      @if (totals(); as t) {
        <div class="bo-metrics">
          <div class="bo-metric bo-metric--lead">
            <span class="bo-metric__label">{{ i18n.t('mfg.lines.count') }}</span>
            <strong class="bo-metric__value mono">{{ t.lines }}</strong>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('mfg.lines.employees') }}</span>
            <strong class="bo-metric__value mono">{{ t.employees }}</strong>
          </div>
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('mfg.lines.activeBoms') }}</span>
            <strong class="bo-metric__value mono">{{ t.activeBoms }}</strong>
          </div>
        </div>
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !pager.loaded()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!pager.summary()?.lines) {
          <app-table-state kind="empty" [message]="i18n.t(mayCreate() ? 'mfg.lines.none' : 'mfg.lines.noneReachable')">
            @if (mayCreate()) {
              <button type="button" class="btn-primary" (click)="editor.open(null)">{{ i18n.t('mfg.lines.new') }}</button>
            }
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.common.code') }}</th>
                  <th>{{ i18n.t('mfg.common.name') }}</th>
                  <th>{{ i18n.t('mfg.lines.location') }}</th>
                  <th>{{ i18n.t('mfg.lines.manager') }}</th>
                  <th class="num">{{ i18n.t('mfg.lines.employees') }}</th>
                  <th class="num">{{ i18n.t('mfg.lines.products') }}</th>
                  <th class="num">{{ i18n.t('mfg.lines.activeBoms') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (l of pager.rows(); track l.id) {
                  <tr [class.is-muted]="!l.isActive">
                    <td class="mono" [attr.data-label]="i18n.t('mfg.common.code')"><span dir="ltr">{{ l.code }}</span></td>
                    <td [attr.data-label]="i18n.t('mfg.common.name')">
                      <button type="button" class="ui-link" (click)="openLine.emit(l.id)">{{ name(l) }}</button>
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.lines.location')">
                      <span class="mono" dir="ltr">{{ l.locationCode }}</span> {{ locationName(l) }}
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.lines.manager')">{{ l.managerName || '—' }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.lines.employees')">{{ l.employeeCount }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.lines.products')">{{ l.productCount }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.lines.activeBoms')">{{ l.activeBomCount }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(l.isActive)">{{ i18n.t(l.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + l.code">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="openLine.emit(l.id)">{{ i18n.t('mfg.lines.open') }}</button>
                        @if (l.canEdit) {
                          <button type="button" appMenuItem (click)="editor.open(l)">{{ i18n.t('common.edit') }}</button>
                        }
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

    <app-mfg-line-editor #editor (saved)="saved($event)" />
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MfgLinesComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** A line's page: the parent moves the address there. */
  readonly openLine = output<string>();

  readonly includeInactive = signal(false);
  /** Whether the server lets this reader create lines: the form's own data route answers it. */
  readonly mayCreate = signal(false);

  // Whether a line has a manager, and an active recipe (filtered on the server); the state is the list route's own.
  readonly managerFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly bomFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly filtered = computed(() => this.managerFilter() !== 'all' || this.bomFilter() !== 'all');
  /**
   * The lines a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"). The
   * summary is the tiles, over every line the state choice keeps — as the tiles always counted.
   */
  readonly pager = createServerPager(
    (page) => this.pages.lines({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.lines',
      filters: () => ({ includeInactive: this.includeInactive(), manager: this.managerFilter(), boms: this.bomFilter() }),
    },
  );
  /** The tiles, once the reader reaches a line. */
  readonly totals = computed(() => {
    const s = this.pager.summary();
    return s && s.lines ? s : null;
  });

  /** The list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.managerFilter.set('all');
    this.bomFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.lines.lead'));
    this.api.lineLocations().then(
      () => this.mayCreate.set(true),
      () => this.mayCreate.set(false),
    );
  }

  /** Asks again for the page on screen — after a line is saved. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  name(l: ProductionLineSummary): string {
    return localizedName(l, this.i18n.lang());
  }

  locationName(l: ProductionLineSummary): string {
    return this.i18n.lang() === 'ar' ? l.locationNameAr || l.locationNameFr : l.locationNameFr || l.locationNameAr;
  }

  tone(active: boolean) {
    return toneOf('active', active ? 'Active' : 'Inactive');
  }

  async saved(lineId: string): Promise<void> {
    await this.reload();
    this.openLine.emit(lineId);
  }
}
