import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, MaterialDto } from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';
import { PosExceptionRow, StaleWord } from './production-api.service';

/**
 * Manufacturing › Materials sold at POS (owner, 2026-10-26, decisions 1 and 8): every till sale of something
 * not for sale — recorded as made, never refused — with its product, branch, quantity, receipt and date, and
 * whether the till sold it before the product was withdrawn from sale (stale offline catalogue), after it, or
 * with no withdrawal on file. A branch user sees their own branch only; the server sets it.
 */
@Component({
  selector: 'app-mfg-pos-exceptions',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, DateInputComponent, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="px-from">{{ i18n.t('mfg.reports.from') }}</label>
          <app-date-input id="px-from" [(ngModel)]="from" />
        </div>
        <div class="ui-filter">
          <label for="px-to">{{ i18n.t('mfg.reports.to') }}</label>
          <app-date-input id="px-to" [(ngModel)]="to" />
        </div>
        @if (branches().length > 1) {
          <div class="ui-filter">
            <label for="px-branch">{{ i18n.t('mfg.reports.branch') }}</label>
            <select id="px-branch" [(ngModel)]="branchId">
              <option [ngValue]="null">{{ i18n.t('mfg.reports.allBranches') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ branchName(b) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="px-product">{{ i18n.t('mfg.boms.product') }}</label>
          <select id="px-product" [(ngModel)]="productId">
            <option [ngValue]="null">{{ i18n.t('mfg.reports.allProducts') }}</option>
            @for (p of products(); track p.productId) {
              <option [ngValue]="p.productId">{{ p.sku }} — {{ name(p) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter ui-filter--search">
          <label for="px-sale">{{ i18n.t('mfg.reports.sale') }}</label>
          <input id="px-sale" type="search" dir="ltr" [(ngModel)]="sale" />
        </div>
        <div class="ui-filter">
          <label for="px-stale">{{ i18n.t('mfg.reports.staleFilter') }}</label>
          <select id="px-stale" [(ngModel)]="stale">
            <option [ngValue]="null">{{ i18n.t('mfg.reports.anyStale') }}</option>
            @for (s of staleWords; track s) {
              <option [ngValue]="s">{{ i18n.t('mfg.stale.' + s) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="px-role">{{ i18n.t('mfg.materials.role') }}</label>
          <select id="px-role" [ngModel]="roleFilter()" (ngModelChange)="roleFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (r of rowRoles(); track r) {
              <option [ngValue]="r">{{ i18n.t('mfg.role.' + r) }}</option>
            }
          </select>
        </div>
        <div class="ui-toolbar__end">
          @if (roleFilter() !== 'all') {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-primary" [disabled]="pager.loading()" (click)="show()">{{ i18n.t('mfg.reports.show') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() && report()?.sales) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (report(); as r) {
        <div class="ui-metrics">
          <div class="ui-metric ui-metric--lead"><span class="ui-metric__label">{{ i18n.t('mfg.reports.sales') }}</span><strong class="ui-metric__value mono">{{ r.sales }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.reports.quantity') }}</span><strong class="ui-metric__value mono">{{ qty(r.totalQuantity) }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.stale.stale') }}</span><strong class="ui-metric__value mono">{{ r.staleCount }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.stale.after_withdrawal') }}</span><strong class="ui-metric__value mono">{{ r.afterWithdrawalCount }}</strong></div>
          <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.stale.unknown') }}</span><strong class="ui-metric__value mono">{{ r.unknownCount }}</strong></div>
        </div>
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !report()?.sales) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!report()) {
          <app-table-state kind="loading" />
        } @else if (!report()?.sales) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.reports.noExceptions')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.reports.soldAt') }}</th>
                  <th>{{ i18n.t('mfg.reports.sale') }}</th>
                  <th>{{ i18n.t('mfg.reports.branch') }}</th>
                  <th>{{ i18n.t('mfg.boms.product') }}</th>
                  <th class="num">{{ i18n.t('mfg.reports.quantity') }}</th>
                  <th>{{ i18n.t('mfg.reports.cashier') }}</th>
                  <th>{{ i18n.t('mfg.reports.staleFilter') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (x of pager.rows(); track x.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.reports.soldAt')">
                      {{ i18n.dateTime(x.soldAtUtc) }}
                      <span class="ui-sub">{{ i18n.t('mfg.reports.received', { at: i18n.dateTime(x.receivedAtUtc) }) }}</span>
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.reports.sale')"><span dir="ltr">{{ x.reference }}</span> <span class="ui-sub" dir="ltr">{{ x.deviceCode }}</span></td>
                    <td [attr.data-label]="i18n.t('mfg.reports.branch')">{{ rowBranch(x) }}</td>
                    <td [attr.data-label]="i18n.t('mfg.boms.product')">
                      {{ productName(x) }} <span class="ui-sub mono" dir="ltr">{{ x.sku }} · {{ i18n.t('mfg.role.' + x.role) }}</span>
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.reports.quantity')">{{ qty(x.quantity) }}</td>
                    <td [attr.data-label]="i18n.t('mfg.reports.cashier')">{{ x.cashierName || '—' }}</td>
                    <td [attr.data-label]="i18n.t('mfg.reports.staleFilter')">
                      <app-status [tone]="tone(x.stale)">{{ i18n.t('mfg.stale.' + x.stale) }}</app-status>
                      @if (x.withdrawnAtUtc) {
                        <span class="ui-sub">{{ i18n.t('mfg.reports.withdrawn', { at: i18n.dateTime(x.withdrawnAtUtc) }) }}</span>
                      }
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
export class MfgPosExceptionsComponent implements OnInit {
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly api = inject(ManufacturingApiService);
  private readonly admin = inject(AdminService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly qty = qty;
  readonly staleWords: StaleWord[] = ['stale', 'after_withdrawal', 'unknown'];
  readonly branches = signal<AdminBranch[]>([]);
  readonly products = signal<MaterialDto[]>([]);
  /** The product's manufacturing role, applied as soon as it is chosen. */
  readonly roleFilter = signal<string>('all');
  // The report's own filters, as typed; "Show" sends them (`applied`).
  from: string | null = null;
  to: string | null = null;
  branchId: string | null = null;
  productId: string | null = null;
  sale = '';
  stale: StaleWord | null = null;
  /** The report's filters as last shown: what the server is asked for. */
  private readonly applied = signal(this.typed());
  /**
   * The exceptions a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"): the
   * report's filters as shown and the role run there; a change goes back to page one. A branch user's report is
   * their own branch's, whatever is asked (the server sets it).
   */
  readonly pager = createServerPager(
    (page) => this.pages.posExceptions({ skip: page.skip, take: page.take }, page.filters),
    { key: 'mfg.posExceptions', filters: () => ({ ...this.applied(), role: this.roleFilter() }) },
  );
  /** The report's tiles, over every sale it holds whatever the role chosen; null until the first answer. */
  readonly report = computed(() => this.pager.summary());
  /** The roles the report holds, in the order they first appear. */
  readonly rowRoles = computed(() => this.report()?.roles ?? []);

  /** The report could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.reports.posLead'));
    this.admin.listBranches().then((b) => this.branches.set(b.filter((x) => !x.isWarehouse)), () => undefined);
    // The products a till could sell by mistake: those with a manufacturing role.
    this.api.materials(null, true).then((m) => this.products.set(m.filter((x) => x.role !== 'None')), () => undefined);
  }

  /** The report's filters as typed now. */
  private typed() {
    return {
      from: this.from, to: this.to, branchId: this.branchId, productId: this.productId, sale: this.sale.trim() || null, stale: this.stale,
    };
  }

  /** "Show": the report for the filters as typed, from its first page — asked again even when they are unchanged. */
  async show(): Promise<void> {
    const next = this.typed();
    if (JSON.stringify(next) === JSON.stringify(this.applied())) {
      if (this.pager.index() !== 0) this.pager.reset();
      else await this.pager.reload();
    } else {
      this.applied.set(next);
    }
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  clearFilters(): void {
    this.roleFilter.set('all');
  }

  name(p: MaterialDto): string {
    return localizedName(p, this.i18n.lang());
  }

  branchName(b: AdminBranch): string {
    return localizedName(b, this.i18n.lang());
  }

  rowBranch(x: PosExceptionRow): string {
    return localizedName({ nameAr: x.branchNameAr, nameFr: x.branchNameFr, nameEn: x.branchNameFr }, this.i18n.lang());
  }

  productName(x: PosExceptionRow): string {
    return localizedName({ nameAr: x.productNameAr, nameFr: x.productNameFr, nameEn: x.productNameEn }, this.i18n.lang()) || x.sku;
  }

  tone(stale: StaleWord) {
    return toneOf('posException', stale);
  }
}
