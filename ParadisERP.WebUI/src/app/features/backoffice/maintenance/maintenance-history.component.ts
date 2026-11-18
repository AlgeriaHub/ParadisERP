import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { StatusTone, toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { MaintenanceApi } from './maintenance-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { maintenanceError } from './maintenance-words';

/** An equipment type the history may be read for: the products the reader's jobs name. */
interface HistoryProduct {
  id: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
}

/**
 * Maintenance › Repair history (Phase M7d, MS-38, MNT-10): every job on one equipment type, or on one piece of it named by its
 * reference — dates, kind, units, parts, outside work and hours, and (to `maintenance.costs.view`, under the pay-rate rule) the
 * cost and what of it was capitalised (MS-30). The product and the piece are the address (`?product=&reference=`), so a deep
 * link, Back and Forward land on the same history. A working panel, never folded. Both lists are paged by the server
 * (owner, 2026-11-17: "paginate in backend, not frontend"; ten rows to begin with); they take no filter of their own beyond
 * the product and the piece, because the tiles above — the server's, over every job of the history — would disagree with a
 * narrower table.
 */
@Component({
  selector: 'app-maintenance-history',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="mh-product">{{ i18n.t('maint.job.product') }}</label>
          <select id="mh-product" [ngModel]="product()" (ngModelChange)="pick.emit({ product: $event, reference: null })">
            <option [ngValue]="null">{{ i18n.t('maint.history.chooseProduct') }}</option>
            @for (p of products(); track p.id) {
              <option [ngValue]="p.id">{{ name(p) }} ({{ p.sku }})</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="mh-reference">{{ i18n.t('maint.history.reference') }}</label>
          <input id="mh-reference" type="search" list="mh-pieces" [ngModel]="reference() ?? ''" [disabled]="!product()"
            (change)="pickReference($any($event.target).value)" />
          <datalist id="mh-pieces">
            @for (r of references(); track r) {
              <option [value]="r"></option>
            }
          </datalist>
        </div>
        <div class="ui-toolbar__end">
          <button type="button" class="btn-ghost" [disabled]="jobPager.loading() || !product()" (click)="load()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (!product()) {
        <section class="ui-surface"><app-table-state kind="empty" [message]="i18n.t('maint.history.pickFirst')" /></section>
      } @else if (!history() && !error()) {
        <section class="ui-surface"><app-table-state kind="loading" /></section>
      } @else if (error()) {
        <section class="ui-surface"><app-table-state kind="error" [message]="error()" (retry)="load()" /></section>
      } @else if (history(); as h) {
        <!-- What MS-38 says, in words: per type with an optional reference, or this type tracked piece by piece. -->
        <p class="notice info" role="status">
          @if (h.mode === 'PerPiece') {
            {{ i18n.t(h.trackedPerPiece ? 'maint.history.tracked' : 'maint.history.notTracked',
              { value: i18n.money(h.unitValue), above: i18n.money(h.perPieceAboveValue) }) }}
          } @else {
            {{ i18n.t('maint.history.perType') }}
          }
        </p>

        <div class="ui-metrics">
          <div class="ui-metric ui-metric--lead">
            <span class="ui-metric__label">{{ i18n.t('maint.history.jobs') }}</span>
            <strong class="ui-metric__value mono" dir="ltr">{{ jobPager.total() }}</strong>
            <span class="ui-sub">{{ h.reference ? i18n.t('maint.history.ofPiece', { reference: h.reference }) : name(h) }}</span>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('maint.cost.parts') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(h.parts) }}</strong>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('maint.cost.outsideWork') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(h.outsideWork) }}</strong>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('maint.hours.hours') }}</span>
            <strong class="ui-metric__value mono" dir="ltr">{{ qty(h.labourHours) }}</strong>
          </div>
          @if (h.costsShown) {
            <div class="ui-metric">
              <span class="ui-metric__label">{{ i18n.t('maint.reports.cost') }}</span>
              <strong class="ui-metric__value" [class.mono]="h.cost !== null">{{ h.cost !== null ? i18n.money(h.cost) : i18n.t('maint.cost.notShown') }}</strong>
              @if (h.capitalised) {
                <span class="ui-sub">{{ i18n.t('maint.history.capitalised', { amount: i18n.money(h.capitalised) }) }}</span>
              }
            </div>
          }
        </div>

        @if (!h.reference && references().length) {
          <section class="ui-surface" aria-labelledby="mh-pieces-title">
            <div class="ui-surface__head">
              <h2 id="mh-pieces-title">{{ i18n.t('maint.history.pieces') }}</h2>
            </div>
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('maint.history.reference') }}</th>
                    <th class="num">{{ i18n.t('maint.history.jobs') }}</th>
                    <th>{{ i18n.t('maint.history.last') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (p of piecePager.rows(); track p.reference) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maint.history.reference')">
                        <button type="button" class="ui-link" (click)="pickReference(p.reference)"><span class="mono" dir="ltr">{{ p.reference }}</span></button>
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.history.jobs')"><span dir="ltr">{{ p.jobs }}</span></td>
                      <td class="mono" [attr.data-label]="i18n.t('maint.history.last')">{{ i18n.date(p.lastAtUtc) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </section>
          <app-list-footer [pager]="piecePager" [loading]="!piecePager.loaded()" />
        }

        <section class="ui-surface" aria-labelledby="mh-jobs-title">
          <div class="ui-surface__head">
            <h2 id="mh-jobs-title">{{ i18n.t(h.reference ? 'maint.history.pieceJobs' : 'maint.history.typeJobs') }}</h2>
          </div>
          @if (!jobPager.total()) {
            <app-table-state kind="empty" [message]="i18n.t('maint.history.none')" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('maint.job.number') }}</th>
                    <th>{{ i18n.t('maint.history.dates') }}</th>
                    <th>{{ i18n.t('maint.history.reference') }}</th>
                    <th class="num">{{ i18n.t('maint.reports.units') }}</th>
                    <th>{{ i18n.t('maint.cost.parts') }}</th>
                    <th class="num">{{ i18n.t('maint.cost.outsideWork') }}</th>
                    <th class="num">{{ i18n.t('maint.hours.hours') }}</th>
                    @if (h.costsShown) {
                      <th class="num">{{ i18n.t('maint.reports.cost') }}</th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (j of jobPager.rows(); track j.id) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maint.job.number')">
                        <button type="button" class="ui-link" (click)="openJob.emit(j.id)"><span class="mono" dir="ltr">{{ j.number }}</span></button>
                        <span class="ui-sub">{{ i18n.t('maint.kind.' + j.kind) }} · {{ j.branchName }}</span>
                        <app-status [tone]="tone(j.status)">{{ i18n.t('maint.status.' + j.status) }}</app-status>
                      </td>
                      <td class="mono" [attr.data-label]="i18n.t('maint.history.dates')">
                        {{ i18n.date(j.openedAtUtc) }}
                        @if (j.completedAtUtc) { <span class="ui-sub">{{ i18n.t('maint.history.completedOn', { date: i18n.date(j.completedAtUtc) }) }}</span> }
                      </td>
                      <td [attr.data-label]="i18n.t('maint.history.reference')">
                        @if (j.itemReference) { <span class="mono" dir="ltr">{{ j.itemReference }}</span> } @else { <span class="ui-sub">—</span> }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.reports.units')">
                        <span dir="ltr">{{ qty(j.qty) }}</span>
                        <span class="ui-sub">{{ i18n.t('maint.reports.repairedScrapped', { repaired: qty(j.repairedQty), scrapped: qty(j.scrappedQty) }) }}</span>
                      </td>
                      <td [attr.data-label]="i18n.t('maint.cost.parts')">
                        <span class="mono">{{ i18n.money(j.parts) }}</span>
                        @for (p of j.partLines; track p.productId) {
                          <span class="ui-sub">{{ name(p) }} × <span dir="ltr">{{ qty(p.qty) }}</span></span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.cost.outsideWork')">{{ i18n.money(j.outsideWork) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.hours.hours')"><span dir="ltr">{{ qty(j.labourHours) }}</span></td>
                      @if (h.costsShown) {
                        <td class="num mono" [attr.data-label]="i18n.t('maint.reports.cost')">
                          {{ j.cost !== null ? i18n.money(j.cost) : i18n.t('maint.cost.notShown') }}
                          @if (j.capitalised) {
                            <span class="ui-sub">{{ i18n.t('maint.history.capitalised', { amount: i18n.money(j.capitalised) }) }}</span>
                          }
                        </td>
                      }
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
        <app-list-footer [pager]="jobPager" [loading]="!jobPager.loaded()" />
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenanceHistoryComponent implements OnInit {
  private readonly api = inject(MaintenanceApi);
  private readonly pages = inject(MaintenancePagesApi);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** The address's product and piece. */
  readonly product = input<string | null>(null);
  readonly reference = input<string | null>(null);
  /** Another product or piece: the address changes, the history follows it. */
  readonly pick = output<{ product: string | null; reference: string | null }>();
  readonly openJob = output<string>();

  readonly products = signal<HistoryProduct[]>([]);

  /**
   * The jobs of the type or the piece, a page at a time from the server (owner, 2026-11-17); another product or piece starts
   * at page one. Nothing is read until a type is chosen. The summary is the history's head (tiles over every job of it) and
   * the references the piece box offers.
   */
  readonly jobPager = createServerPager(
    (page) => this.pages.historyJobs({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'maintenance.history.jobs',
      filters: () => ({ productId: this.product(), reference: this.reference() }),
      enabled: () => !!this.product(),
      debounceMs: 0,
    },
  );
  /** The pieces of the type that name a reference: only read for the type itself, not for one piece. */
  readonly piecePager = createServerPager(
    (page) => this.pages.historyPieces({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'maintenance.history.pieces',
      filters: () => ({ productId: this.product(), reference: this.reference() }),
      enabled: () => !!this.product() && !this.reference(),
      debounceMs: 0,
    },
  );

  /** The history's head: what MS-38 says, and the tiles over every job of it. */
  readonly history = computed(() => (this.product() ? this.jobPager.summary()?.history ?? null : null));
  /** The piece references the reference box offers. */
  readonly references = computed(() => this.jobPager.summary()?.references ?? []);
  /** The history could not be read, in words. */
  readonly error = computed(() => {
    const e = this.product() ? this.jobPager.error() : null;
    return e ? maintenanceError(e, (k) => this.i18n.t(k)) : null;
  });

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('maint.history.lead'));
    try {
      // The types the reader's jobs name: the company's equipment, never a customer's (a picker, not a list).
      const jobs = await this.api.jobs();
      const seen = new Map<string, HistoryProduct>();
      for (const j of jobs) {
        if (j.kind === 'Customer' || !j.productId || seen.has(j.productId)) continue;
        seen.set(j.productId, { id: j.productId, sku: j.productSku, nameAr: j.productNameAr, nameFr: j.productNameFr, nameEn: j.productNameEn });
      }
      this.products.set([...seen.values()].sort((a, b) => a.sku.localeCompare(b.sku)));
    } catch {
      this.products.set([]);
    }
  }

  /** Refresh: the pages on screen, asked again. */
  async load(): Promise<void> {
    if (!this.product()) return;
    await Promise.all([this.jobPager.reload(), this.reference() ? Promise.resolve() : this.piecePager.reload()]);
  }

  pickReference(value: string | null): void {
    const reference = value?.trim() || null;
    if (reference === this.reference()) return;
    this.pick.emit({ product: this.product(), reference });
  }

  tone(status: string): StatusTone {
    return toneOf('maintenanceJob', status);
  }

  name(x: { nameAr: string; nameFr: string; nameEn: string; sku: string }): string {
    return localizedName(x, this.i18n.lang()) || x.sku;
  }

  qty(value: number): string {
    return formatQty(value, this.i18n.lang());
  }
}
