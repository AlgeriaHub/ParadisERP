import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { RegionalService } from '../../../core/regional.service';
import { localDay } from '../../../core/rentals';
import { DateInputComponent } from '../../../ui/date-input.component';
import { DialogComponent } from '../../../ui/dialog.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay } from '../backoffice-format';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, ProductionLineSummary } from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { MFG, mfgCan, mfgError, qty } from './manufacturing-shared';
import { ProductionApiService, ProductionLogStatus } from './production-api.service';

/**
 * Manufacturing › Daily production records (Phase 2; owner, 2026-10-26, decision 4): one record per line per
 * day — the paper sheet's replacement — listed newest first with its state, rows and hours. Opening a day
 * opens that line's record for it, or starts it.
 */
@Component({
  selector: 'app-mfg-records',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, DateInputComponent, DialogComponent, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    @if (canSubmit() && enabled()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="openDay()"><span aria-hidden="true">+</span> {{ i18n.t('mfg.records.openDay') }}</button>
      </ng-template>
    }
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="pl-line">{{ i18n.t('mfg.boms.line') }}</label>
          <select id="pl-line" [ngModel]="lineFilter()" (ngModelChange)="lineFilterChange.emit($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.orders.allLines') }}</option>
            @for (l of lines(); track l.id) {
              <option [ngValue]="l.id">{{ l.code }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="pl-status">{{ i18n.t('common.state') }}</label>
          <select id="pl-status" [ngModel]="status()" (ngModelChange)="status.set($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.boms.allStates') }}</option>
            @for (s of statuses; track s) {
              <option [ngValue]="s">{{ i18n.t('mfg.logStatus.' + s) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="pl-from">{{ i18n.t('filters.from') }}</label>
          <app-date-input id="pl-from" [ngModel]="dayFrom()" (ngModelChange)="dayFrom.set($event || '')" />
        </div>
        <div class="ui-filter">
          <label for="pl-to">{{ i18n.t('filters.to') }}</label>
          <app-date-input id="pl-to" [ngModel]="dayTo()" (ngModelChange)="dayTo.set($event || '')" />
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
      @if (!enabled()) {
        <p class="notice warn" role="status">{{ i18n.t('mfg.operations.off') }}</p>
      }
      @if (error() && pager.loaded()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !pager.loaded()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.records.none')">
            @if (canSubmit() && enabled()) {
              <button type="button" class="btn-primary" (click)="openDay()">{{ i18n.t('mfg.records.openDay') }}</button>
            }
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.records.day') }}</th>
                  <th>{{ i18n.t('mfg.boms.line') }}</th>
                  <th>{{ i18n.t('mfg.orders.number') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="num">{{ i18n.t('mfg.records.rows') }}</th>
                  <th class="num">{{ i18n.t('mfg.records.hours') }}</th>
                  <th>{{ i18n.t('mfg.records.approvedBy') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (l of pager.rows(); track l.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.records.day')">
                      <button type="button" class="ui-link" (click)="open.emit(l.id)">{{ day(l.day) }}</button>
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.line')">{{ l.lineCode }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.orders.number')"><span dir="ltr">{{ l.number }}</span></td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(l.status)">{{ i18n.t('mfg.logStatus.' + l.status) }}</app-status>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.records.rows')">{{ l.rowCount }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.records.hours')">{{ qty(l.hours) }}</td>
                    <td [attr.data-label]="i18n.t('mfg.records.approvedBy')">{{ l.approvedBy || '—' }}</td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + l.number">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="open.emit(l.id)">{{ i18n.t('mfg.records.open') }}</button>
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

    <app-dialog [open]="opening()" size="sm" [heading]="i18n.t('mfg.records.openDay')" (dismiss)="opening.set(false)">
      @if (opening()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <div class="bo-field">
            <label for="pl-f-line">{{ i18n.t('mfg.boms.line') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <select id="pl-f-line" [(ngModel)]="newLine">
              @for (l of activeLines(); track l.id) {
                <option [ngValue]="l.id">{{ l.code }} — {{ lineName(l) }}</option>
              }
            </select>
          </div>
          <div class="bo-field">
            <label for="pl-f-day">{{ i18n.t('mfg.records.day') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <app-date-input id="pl-f-day" [(ngModel)]="newDay" />
          </div>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="opening.set(false)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy() || !newLine || !newDay" (click)="confirmOpen()">{{ i18n.t('mfg.records.open') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .editor {
        display: grid;
        gap: var(--sp-3);
      }
    `,
  ],
})
export class MfgRecordsComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly production = inject(ProductionApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly auth = inject(AuthService);
  private readonly regional = inject(RegionalService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly lineFilter = input<string | null>(null);
  readonly open = output<string>();
  readonly lineFilterChange = output<string | null>();

  readonly statuses: ProductionLogStatus[] = ['Draft', 'Submitted', 'Approved'];
  readonly qty = qty;
  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly enabled = signal(true);
  readonly busy = signal(false);
  readonly formError = signal<string | null>(null);
  readonly status = signal<ProductionLogStatus | null>(null);
  readonly opening = signal(false);
  newLine = '';
  newDay = '';

  readonly canSubmit = computed(() => mfgCan(this.auth.currentUser(), MFG.logSubmit));
  readonly activeLines = computed(() => this.lines().filter((l) => l.isActive));

  // The record's day between two company days (ISO), compared on the server; the state is the list route's own filter.
  readonly dayFrom = signal('');
  readonly dayTo = signal('');
  readonly filtered = computed(() => !!this.dayFrom() || !!this.dayTo());
  /**
   * The records a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"):
   * the line in the address, the state and the days run there; a change goes back to page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.logs({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.records',
      filters: () => ({ lineId: this.lineFilter(), status: this.status(), from: this.dayFrom(), to: this.dayTo() }),
    },
  );

  /** The list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.dayFrom.set('');
    this.dayTo.set('');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.records.lead'));
    this.production.operationsStatus().then((s) => this.enabled.set(s.stockOperationsEnabled), () => undefined);
    this.api.lines(true).then((l) => this.lines.set(l), () => undefined);
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  lineName(l: ProductionLineSummary): string {
    return localizedName(l, this.i18n.lang());
  }

  day(value: string): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  tone(status: ProductionLogStatus) {
    return toneOf('productionLog', status);
  }

  openDay(): void {
    this.newLine = this.lineFilter() ?? this.activeLines()[0]?.id ?? '';
    this.newDay = localDay(new Date().toISOString(), this.regional.settings().timeZone);
    this.formError.set(null);
    this.opening.set(true);
  }

  async confirmOpen(): Promise<void> {
    this.busy.set(true);
    this.formError.set(null);
    try {
      const log = await this.production.openLog(this.newLine, this.newDay);
      this.opening.set(false);
      this.open.emit(log.log.id);
    } catch (e) {
      this.formError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
