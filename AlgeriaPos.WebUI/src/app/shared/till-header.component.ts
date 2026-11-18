import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { BranchFace } from '../core/branch-face';
import { cashBoxOnly } from '../core/cash-box';
import { I18nService } from '../core/i18n.service';
import { landingPath } from '../core/landing';
import { SyncService } from '../core/sync.service';
import { ConfirmService } from '../ui/confirm.service';
import { MENU } from '../ui/menu.component';
import { ConnectionStatusComponent } from './connection-status.component';
import { AppearanceDialogComponent } from './appearance-dialog.component';
import { LanguageSelectComponent } from './language-select.component';
import { confirmSignOut } from './sign-out';

/**
 * The till's own header (REDESIGN_PLAN.md §12): the bar the four till pages share, and nothing of
 * the back office. It replaces the old top bar on `/register`, `/sales`, `/products` and `/sync`
 * only, so the till's addresses, its offline behaviour and the height it sizes itself against
 * (`--topbar-h`) are unchanged.
 *
 * - The four till pages are its tab strip; the module links of the back office are gone from it.
 * - A manager or admin gets one way into the back office, a cashier who holds the cash box one
 *   way to the vouchers, as before.
 * - The connection state is still a word, with the count of sales waiting beside the Sync tab.
 */
@Component({
  selector: 'app-till-header',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, ConnectionStatusComponent, LanguageSelectComponent, AppearanceDialogComponent, ...MENU],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="topbar">
      <!-- A. Where this till is: the branch, and under it what the screen is. -->
      <div class="context">
        @if (face.logo(); as logo) {
          <img class="logo-img" [src]="logo" [alt]="face.name() || i18n.t('app.title')" />
        } @else {
          <span class="mark" aria-hidden="true">{{ i18n.countryCode() }}</span>
        }
        <div class="context-text">
          <strong class="branch" dir="auto">{{ face.name() || i18n.t('app.title') }}</strong>
          <span class="context-meta">
            @if (face.name()) {
              <span>{{ i18n.t('app.title') }}</span>
            }
            <!-- The country profile's code, once the large mark: now a small tag on this line. -->
            <span class="cc" aria-hidden="true">{{ i18n.countryCode() }}</span>
          </span>
        </div>
      </div>

      <!-- B. The till's four pages. On a phone the three after Sell fold into "More", except the
           one being shown, so Sell and the current page always stay in the bar. -->
      <nav class="nav" [attr.aria-label]="i18n.t('app.title')">
        <a class="tab" routerLink="/register" routerLinkActive="active" ariaCurrentWhenActive="page">{{ i18n.t('nav.register') }}</a>
        <a class="tab folds" routerLink="/sales" routerLinkActive="active" ariaCurrentWhenActive="page">{{ i18n.t('nav.sales') }}</a>
        <a class="tab folds" routerLink="/products" routerLinkActive="active" ariaCurrentWhenActive="page">{{ i18n.t('nav.products') }}</a>
        <a class="tab folds" routerLink="/sync" routerLinkActive="active" ariaCurrentWhenActive="page">
          {{ i18n.t('nav.sync') }}
          @if (pending() > 0) {
            <!-- The count is decoration for the eye; the words beside it are what a screen
                 reader announces, so the state is never a bare number. -->
            <span class="pill" aria-hidden="true">{{ pending() }}</span>
            <span class="sr-only">{{ i18n.t('status.pending', { n: pending() }) }}</span>
          }
        </a>
        <app-menu class="nav-more" triggerClass="more-trigger" [label]="i18n.t('nav.more')">
          <span menuTrigger class="more">
            <span aria-hidden="true">≡</span>
            {{ i18n.t('nav.more') }}
            @if (pending() > 0) {
              <span class="pill" aria-hidden="true">{{ pending() }}</span>
            }
          </span>
          <a appMenuItem routerLink="/sales" routerLinkActive="is-current" ariaCurrentWhenActive="page">{{ i18n.t('nav.sales') }}</a>
          <a appMenuItem routerLink="/products" routerLinkActive="is-current" ariaCurrentWhenActive="page">{{ i18n.t('nav.products') }}</a>
          <a appMenuItem routerLink="/sync" routerLinkActive="is-current" ariaCurrentWhenActive="page">
            {{ i18n.t('nav.sync') }}
            @if (pending() > 0) {
              <span class="pill" aria-hidden="true">{{ pending() }}</span>
              <span class="sr-only">{{ i18n.t('status.pending', { n: pending() }) }}</span>
            }
          </a>
        </app-menu>
      </nav>

      <!-- C. Utilities: the state in words, then the language, the back office and the account. -->
      <div class="tools">
        <app-connection-status tone="ink" />
        <app-language-select class="lang-wide" tone="ink" />
        @if (canManage()) {
          <a class="office" [routerLink]="officePath()">{{ i18n.t('nav.backoffice') }}</a>
        } @else if (cashBoxOnly()) {
          <a class="office" routerLink="/backoffice/finance/vouchers">{{ i18n.t('nav.cashBox') }}</a>
        }
        <app-menu class="user-menu" triggerClass="user-trigger" [label]="i18n.t('nav.account')">
          <!-- One control: the name and its caret are the same button. -->
          <span menuTrigger class="user">
            <span class="user-name" dir="auto">{{ auth.currentUser()?.fullName }}</span>
            <span class="caret" aria-hidden="true">▾</span>
          </span>
          <p class="who" role="presentation">
            <strong>{{ auth.currentUser()?.fullName }}</strong>
            <span>{{ roleLabel() }}</span>
          </p>
          <!-- Below a wide desktop the way into the back office and the language move in here,
               so the bar keeps its one row there and its two short rows on a tablet or phone. -->
          @if (canManage()) {
            <a appMenuItem class="office-narrow" [routerLink]="officePath()">{{ i18n.t('nav.backoffice') }}</a>
          } @else if (cashBoxOnly()) {
            <a appMenuItem class="office-narrow" routerLink="/backoffice/finance/vouchers">{{ i18n.t('nav.cashBox') }}</a>
          }
          <div class="lang-narrow" role="presentation"><app-language-select /></div>
          <!-- The new design behind its switch (owner, 2026-10-08): a cashier never reaches the back
               office, so the till offers it too. -->
          <button type="button" appMenuItem (click)="appearanceOpen.set(true)">{{ i18n.t('appearance.open') }}</button>
          <button type="button" appMenuItem class="sign-out" (click)="logout()">{{ i18n.t('nav.logout') }}</button>
        </app-menu>
      </div>
    </header>
    <!-- Deferred: the dialog is downloaded the first time it is opened, not with the till. -->
    @if (appearanceOpen()) {
      @defer {
        <app-appearance-dialog [open]="true" (closed)="appearanceOpen.set(false)" />
      }
    }
  `,
  styleUrl: './till-header.component.scss',
})
export class TillHeaderComponent {
  readonly i18n = inject(I18nService);
  readonly auth = inject(AuthService);
  readonly face = inject(BranchFace);
  private readonly sync = inject(SyncService);
  private readonly router = inject(Router);
  private readonly confirm = inject(ConfirmService);

  readonly appearanceOpen = signal(false);

  readonly pending = computed(() => this.sync.stats().pending);
  readonly canManage = computed(() => {
    const role = this.auth.currentUser()?.role;
    return role === 'Manager' || role === 'Admin';
  });
  readonly cashBoxOnly = computed(() => cashBoxOnly(this.auth.currentUser()));
  readonly officePath = computed(() => landingPath(this.auth.currentUser()));
  readonly roleLabel = computed(() => {
    const role = this.auth.currentUser()?.role;
    return role ? this.i18n.t('nav.role.' + role.toLowerCase()) : '';
  });

  async logout(): Promise<void> {
    if (!(await confirmSignOut(this.confirm, this.i18n, this.pending()))) return;
    await this.auth.logout();
    await this.router.navigate(['/login']);
  }
}
