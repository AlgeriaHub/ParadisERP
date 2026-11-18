import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { BranchFace } from '../../core/branch-face';
import { BranchLock } from '../../core/branch-lock';
import { BranchScopeService } from '../../core/branch-scope.service';
import { cashBoxOnly } from '../../core/cash-box';
import { ConnectivityService } from '../../core/connectivity.service';
import { I18nService } from '../../core/i18n.service';
import { NavPage, findPage, moduleOf } from '../../core/nav';
import { HeaderCrumb, PageHeaderComponent } from '../../ui/page-header.component';
import { BackofficeBranchesComponent } from './branches-tab.component';
import { BackofficeCustomersComponent } from './customers-tab.component';
import { BackofficeDevicesComponent } from './devices-tab.component';
import { BackofficeGiftCardsComponent } from './gift-cards-tab.component';
import { BackofficeLowStockComponent } from './low-stock.component';
import { BackofficeMissingCostComponent } from './missing-cost.component';
import { BackofficeFixedAssetsComponent } from './fixed-assets/fixed-assets.component';
import { BackofficeStockValuationComponent } from './stock-valuation.component';
import { BookClosingComponent } from './finance/book-closing.component';
import { QuantityStatementComponent } from './customers/quantity-statement.component';
import { ItemStatusComponent } from './item-status/item-status.component';
import { BackofficeStockTransfersComponent } from './stock/stock-transfers.component';
import { BackofficeProductsComponent } from './products-tab.component';
import { BackofficeReportsComponent } from './reports-tab.component';
import { BackofficeStockComponent } from './stock-tab.component';
import { BackofficeStoreComponent } from './store-tab.component';
import { BackofficeSubscriptionComponent } from './subscription-tab.component';
import { AwaitingCollectionComponent } from './finance/awaiting-collection.component';
import { CashBoxesComponent } from './finance/cash-boxes.component';
import { PaymentMethodsComponent } from './finance/payment-methods.component';
import { BackofficeUsersComponent } from './users-tab.component';
import { BackofficeRolesComponent } from './roles/roles.component';
import { BackofficeAccountingComponent } from './accounting-tab.component';
import { BackofficeRentalsComponent } from './rentals/rentals-tab.component';
import { BackofficeReceivablesComponent } from './receivables/receivables-tab.component';
import { BackofficePaymentVouchersComponent } from './receivables/payment-vouchers-tab.component';
import { BackofficeSuppliersComponent } from './suppliers/suppliers-tab.component';
import { VoucherCategoriesComponent } from './receivables/voucher-categories.component';
import { CustomerAdvanceSettingsComponent } from './receivables/customer-advance-settings.component';
import { PayrollTabComponent } from './payroll/payroll-tab.component';
import { DashboardComponent } from './dashboard/dashboard.component';
import { BackofficeManufacturingComponent } from './manufacturing/manufacturing-tab.component';
import { BackofficeMaintenanceComponent } from './maintenance/maintenance-tab.component';
import { BackofficeNavState } from './shell/backoffice-nav-state';
import { PageChrome } from './shell/page-chrome';

/**
 * One back-office page: its header, and the existing section component it mounts on the chip the
 * route names (REDESIGN_PLAN.md §4). This is the old shell's `@switch`, kept whole — the same
 * components, the same chunks for the five heavy ones — with the section and the chip coming from
 * the address instead of from a rail held in memory.
 *
 * Nothing inside the sections changes in Phase 2 except that they can be opened on a chip; their
 * internal views (a contract, an invoice, a payroll run) become addresses of their own in
 * Phases 3–4.
 */
@Component({
  selector: 'app-backoffice-page',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    PageHeaderComponent,
    BackofficeReportsComponent,
    BackofficeStoreComponent,
    BackofficeBranchesComponent,
    BackofficeDevicesComponent,
    BackofficeProductsComponent,
    BackofficeCustomersComponent,
    BackofficeUsersComponent,
    BackofficeRolesComponent,
    BackofficeSubscriptionComponent,
    BackofficeGiftCardsComponent,
    AwaitingCollectionComponent,
    CashBoxesComponent,
    PaymentMethodsComponent,
    BackofficeStockComponent,
    BackofficeLowStockComponent,
    BackofficeMissingCostComponent,
    BackofficeFixedAssetsComponent,
    BackofficeStockValuationComponent,
    BookClosingComponent,
    QuantityStatementComponent,
    ItemStatusComponent,
    BackofficeStockTransfersComponent,
    BackofficeAccountingComponent,
    BackofficeRentalsComponent,
    BackofficeReceivablesComponent,
    BackofficePaymentVouchersComponent,
    BackofficeSuppliersComponent,
    VoucherCategoriesComponent,
    CustomerAdvanceSettingsComponent,
    PayrollTabComponent,
    DashboardComponent,
    BackofficeManufacturingComponent,
    BackofficeMaintenanceComponent,
  ],
  template: `
    @if (page(); as p) {
      <div class="page">
        <app-page-header
          [icon]="icon()"
          [heading]="chrome.heading() || i18n.t(p.label)"
          [crumbs]="crumbs()"
          [crumbsLabel]="i18n.t('nav.breadcrumb')"
          [description]="chrome.description()"
          [scope]="scope()"
        >
          <!-- A section's primary action and filters, declared in the section with
               appPageActions / appPageToolbar (shell/page-chrome.ts). -->
          <ng-container ngProjectAs="[pageActions]"><ng-container [ngTemplateOutlet]="chrome.actions()" /></ng-container>
          <ng-container ngProjectAs="[pageToolbar]"><ng-container [ngTemplateOutlet]="chrome.toolbar()" /></ng-container>
        </app-page-header>

        @if (!connectivity.online()) {
          <p class="notice warn">{{ i18n.t('backoffice.onlineOnly') }}</p>
        }

        @switch (p.section) {
          @case ('dashboard') {
            <!-- Its own chunk: eight small blocks over endpoints other pages own. -->
            @defer (on immediate) {
              <app-dashboard />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('reports') { <app-backoffice-reports /> }
          @case ('subscription') { <app-backoffice-subscription /> }
          @case ('giftCards') { <app-backoffice-gift-cards /> }
          @case ('treasury') {
            <!-- Finance's own pages: what is still on paper, and where the cash is. -->
            @if (p.view === 'pending') {
              <app-awaiting-collection />
            } @else {
              <app-cash-boxes [path]="subpath()" />
            }
          }
          @case ('financeSettings') {
            <!-- Finance's settings only: the tenders, which expenses must name a contract, and customer advances. -->
            <div class="bo-stack">
              <app-payment-methods />
              <app-voucher-categories />
              <!-- M7b: where a customer advance is held and when its VAT is due (the accountant's, accounting.manage). -->
              <app-customer-advance-settings />
            </div>
          }
          @case ('accounting') {
            <!-- Its own chunk: the books are five reports and a journal, and the manager who
                 only ever edits the catalogue should not download them to do it. -->
            @defer (on immediate) {
              <app-backoffice-accounting
                [page]="$any(p.view)"
                [account]="account()"
                (goTreasury)="go('finance/awaiting-collection')"
                (pageChange)="openReport($event)"
              />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('rentals') {
            <!-- Its own chunk: the rental module is a contract, four document screens and the
                 arithmetic that prices them, and the cashier's terminal downloads none of it. -->
            @defer (on immediate) {
              <app-backoffice-rentals [page]="$any(p.view)" [path]="subpath()" />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('receivables') {
            <!-- Its own chunk: invoices, receipts, credit notes and the debtor book. -->
            @defer (on immediate) {
              <app-backoffice-receivables
                [page]="$any(p.view)"
                [start]="customer()"
                [reset]="reselected()"
                [receiptsOnly]="cashOnly()"
                [path]="subpath()"
              />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('paymentVouchers') {
            <!-- Its own chunk, like the debtor book it sits beside: money going out. -->
            @defer (on immediate) {
              <app-backoffice-payment-vouchers [path]="subpath()" />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('suppliers') {
            @defer (on immediate) {
              <app-backoffice-suppliers />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('personnel') {
            <!-- Its own chunk: the figures every payslip is computed against, the advances,
                 the payday and what was withheld. -->
            @defer (on immediate) {
              <app-payroll-tab [page]="$any(p.view)" />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('manufacturing') {
            <!-- Its own chunk: lines, recipes and their settings, which only a manufacturing reader opens. -->
            @defer (on immediate) {
              <app-backoffice-manufacturing [page]="$any(p.view)" [path]="subpath()" />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('maintenance') {
            <!-- Its own chunk: the settings (M0), the jobs and the waiting list (M1) — docs/maintenance/plan.md. -->
            @defer (on immediate) {
              <app-backoffice-maintenance [page]="$any(p.view)" [path]="subpath()" />
            } @loading {
              <p class="notice">{{ i18n.t('common.loading') }}</p>
            }
          }
          @case ('stock') { <app-backoffice-stock [page]="$any(p.view)" /> }
          @case ('lowStock') { <app-backoffice-low-stock /> }
          @case ('missingCost') { <app-backoffice-missing-cost /> }
          @case ('fixedAssets') { <app-backoffice-fixed-assets /> }
          @case ('stockValuation') { <app-backoffice-stock-valuation /> }
          @case ('closing') { <app-book-closing /> }
          @case ('customerQuantities') { <app-quantity-statement /> }
          @case ('itemStatus') { <app-item-status /> }
          @case ('transfers') { <app-backoffice-stock-transfers /> }
          @case ('store') { <app-backoffice-store /> }
          @case ('branches') { <app-backoffice-branches /> }
          @case ('devices') { <app-backoffice-devices /> }
          @case ('products') { <app-backoffice-products /> }
          @case ('customers') { <app-backoffice-customers (openAccount)="openAccount($event)" /> }
          @case ('users') { <app-backoffice-users /> }
          @case ('roles') { <app-backoffice-roles [path]="subpath()" /> }
        }
      </div>
    }
  `,
  providers: [PageChrome],
  styleUrls: ['./backoffice-ui.scss'],
  styles: [
    `
      .page {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--sp-4);
        align-content: start;
        min-inline-size: 0;
      }
    `,
  ],
})
export class BackofficePageComponent {
  readonly i18n = inject(I18nService);
  readonly connectivity = inject(ConnectivityService);
  readonly chrome = inject(PageChrome);
  private readonly auth = inject(AuthService);
  private readonly lock = inject(BranchLock);
  private readonly face = inject(BranchFace);
  private readonly branchScope = inject(BranchScopeService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly state = inject(BackofficeNavState);

  private readonly data = toSignal(this.route.data, { initialValue: this.route.snapshot.data });
  private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });

  readonly page = computed<NavPage | undefined>(() => findPage(this.data()['pageKey'] as string));
  readonly icon = computed(() => {
    const p = this.page();
    return p ? moduleOf(p.key)?.icon ?? null : null;
  });
  readonly crumbs = computed<HeaderCrumb[]>(() => {
    const page = this.page();
    const module = page ? moduleOf(page.key) : undefined;
    if (!module || !page) return [];
    // With an entity open (a contract, a note), the page's name becomes the way back to its list.
    const entity = !!this.chrome.heading();
    const pageCrumb: HeaderCrumb = entity
      ? { label: this.i18n.t(page.label), link: '/backoffice/' + page.path }
      : this.i18n.t(page.label);
    // A one-page module (the dashboard) is its page: naming it twice says nothing, and a trail of
    // one would only repeat the heading under it.
    const moduleCrumb = module.pages.length > 1 ? [this.i18n.t(module.label)] : [];
    const trail = [...moduleCrumb, pageCrumb, ...this.chrome.trail()];
    return trail.length > 1 ? trail : [];
  });

  /**
   * The segments after the page's own address, for a page whose entities and documents are
   * addresses of their own (`deep` in core/nav.ts): `['c1']`, `['c1', 'issue']`, `['new']`…
   */
  private readonly segments = toSignal(this.route.url, { initialValue: this.route.snapshot.url });
  readonly subpath = computed(() => {
    const page = this.page();
    if (!page?.deep) return [];
    return this.segments()
      .map((s) => s.path)
      .slice(page.path.split('/').length);
  });

  /**
   * Whose data the page shows (§8.3). Company-wide data says so to everyone. A locked user's
   * branch pages are their branch, said by name. The Admin's pages whose filter is the working
   * branch (`working` in core/nav.ts) say what it resolves to there: one branch by name, or all
   * branches only where the endpoint takes it. Other Admin pages still let their own filter say it.
   */
  readonly scope = computed<string | null>(() => {
    const page = this.page();
    const scope = page?.scope;
    if (!page || !scope || scope === 'none') return null;
    if (scope === 'company') return this.i18n.t('scope.company');
    // A document open under a branch page (a contract, a voucher, a receipt, a box) is one branch's
    // and says which itself; the list's scope — "All branches", or the reader's own branch for a
    // document of another one read by its address — would misname it.
    if (this.chrome.heading()) return null;
    if (this.lock.locked()) return this.i18n.t('scope.branch', { branch: this.face.name() || '—' });
    if (!page.working) return null;
    const resolved = this.branchScope.resolve(scope);
    if (resolved.label === 'all') return this.i18n.t('scope.all');
    if (resolved.label === 'group') return this.i18n.t('scope.group');
    return this.i18n.t('scope.branch', { branch: this.face.nameOf(resolved.branchId) || '—' });
  });

  readonly cashOnly = computed(() => cashBoxOnly(this.auth.currentUser()));
  /** The customer the customers list's "account" button asked for (`?customer=`). */
  readonly customer = computed(() => this.query().get('customer'));
  /** The account a report row opened the statement on (`?account=`). */
  readonly account = computed(() => this.query().get('account'));
  readonly reselected = computed(() => {
    const page = this.page();
    return page ? this.state.reselected(page.key) : 0;
  });

  go(path: string): void {
    void this.router.navigate(['/backoffice', ...path.split('/')]);
  }

  openAccount(customerId: string): void {
    void this.router.navigate(['/backoffice', 'customers', 'accounts'], { queryParams: { customer: customerId } });
  }

  /** A trial-balance or P&L row opening an account's statement: the address follows. */
  openReport(change: { view: string; account: string | null }): void {
    if (change.view === 'statement') {
      void this.router.navigate(['/backoffice', 'reports', 'account-statement'], {
        queryParams: change.account ? { account: change.account } : {},
      });
    }
  }
}
