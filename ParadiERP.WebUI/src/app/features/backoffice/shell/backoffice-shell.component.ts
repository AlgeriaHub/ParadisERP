import { Component, ElementRef, OnDestroy, computed, effect, inject, signal, viewChild } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from '../../../core/auth.service';
import { BranchFace } from '../../../core/branch-face';
import { ProfileService } from '../../../core/profile.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { moduleTone } from '../../../core/nav-tones';
import { ConnectionStatusComponent } from '../../../shared/connection-status.component';
import { LanguageSelectComponent } from '../../../shared/language-select.component';
import { AppearanceDialogComponent } from '../../../shared/appearance-dialog.component';
import { confirmSignOut } from '../../../shared/sign-out';
import { SyncService } from '../../../core/sync.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { MENU } from '../../../ui/menu.component';
import { BackofficeNavState } from './backoffice-nav-state';
import { NavSearchComponent } from './nav-search.component';
import { SidebarComponent } from './sidebar.component';
import { WorkingBranchComponent } from './working-branch.component';

/**
 * The back-office shell (Paradis redesign, owner 2026-11-12):
 * - Two-panel menu on the inline-start side for the full height.
 * - Main column wearing the open module's section tone, with a white 64px sticky top bar
 *   holding the sidebar toggle, page search, working branch, connection state, language picker and user menu.
 * - Mobile drawer (< 768px) with rail and panel both.
 */
@Component({
  selector: 'app-backoffice-shell',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    SidebarComponent,
    NavSearchComponent,
    WorkingBranchComponent,
    ConnectionStatusComponent,
    LanguageSelectComponent,
    AppearanceDialogComponent,
    ...MENU,
  ],
  template: `
    <div class="app">
      <aside class="shell-sidebar">
        <app-bo-sidebar [collapsed]="sidebarCollapsed()" (toggleRequested)="toggleSidebar()" />
      </aside>

      <!-- The main column wears the open module's section tone: page icon, toolbar wash, card headers, table
           headers, count pills, the active tab and stat tiles inside follow it. Buttons, links and inputs stay brand. -->
      <div class="main-col" [class]="tone()">
        <header class="bar">
          <button
            type="button"
            class="bar__menu"
            (click)="openDrawer()"
            aria-haspopup="dialog"
            [attr.aria-label]="i18n.t('nav.menu')"
          >
            <svg
              viewBox="0 0 24 24"
              width="22"
              height="22"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              aria-hidden="true"
            >
              <path d="M4 6h16M4 12h16M4 18h16" />
            </svg>
          </button>
          <!-- The fold is the arrow on the menu's inner edge (owner, 2026-11-12). -->
          <app-bo-nav-search class="bar__search" />
          <span class="bar__spacer"></span>
          <app-working-branch class="bar__branch" />
          <app-connection-status class="bar__conn" />
          <app-language-select class="bar__lang" />
          <app-menu
            class="bar__user gh__user"
            triggerClass="bar__user-trigger"
            [label]="auth.currentUser()?.fullName || i18n.t('nav.account')"
          >
            <span menuTrigger class="user">
              <span class="avatar" [class.has-photo]="!!profile.photo()" aria-hidden="true" dir="auto">
                @if (profile.photo(); as src) {
                  <img [src]="src" alt="" />
                } @else {
                  {{ userInitials() }}
                }
              </span>
              <span class="user__text">
                <strong dir="auto">{{ auth.currentUser()?.fullName }}</strong>
                <small>{{ roleLabel() }}</small>
              </span>
            </span>
            <p class="who" role="presentation">
              <strong>{{ auth.currentUser()?.fullName }}</strong>
              <span>{{ roleLabel() }}</span>
            </p>
            <a appMenuItem routerLink="/register">{{ i18n.t('nav.openTill') }}</a>
            <a appMenuItem routerLink="/account" [queryParams]="{ from: here() }" data-profile>{{ i18n.t('account.profile.title') }}</a>
            <a appMenuItem routerLink="/change-password" [queryParams]="{ from: here() }" data-change-password>
              {{ i18n.t('account.password.title') }}
            </a>
            <button type="button" appMenuItem (click)="appearanceOpen.set(true)">{{ i18n.t('appearance.open') }}</button>
            <hr appMenuSeparator />
            <button type="button" appMenuItem (click)="logout()">{{ i18n.t('nav.logout') }}</button>
          </app-menu>
        </header>

        <main class="work">
          <router-outlet />
        </main>
      </div>

      <dialog
        #drawer
        class="drawer"
        [attr.aria-label]="i18n.t('nav.main')"
        (cancel)="closeDrawer()"
        (click)="onDrawerClick($event)"
      >
        <app-bo-sidebar drawer (navigated)="closeDrawer()" (closeRequested)="closeDrawer()">
          <app-working-branch menuTop class="drawer__branch" />
        </app-bo-sidebar>
      </dialog>
    </div>

    <app-appearance-dialog [open]="appearanceOpen()" (closed)="appearanceOpen.set(false)" />
  `,
  styleUrl: './backoffice-shell.component.scss',
})
export class BackofficeShellComponent implements OnDestroy {
  readonly i18n = inject(I18nService);
  /** The back-office vocabulary travels in this route's chunk, never in a till's download.
   *  A field initialiser, so the keys are in place before the first render. */
  private readonly words = this.i18n.extend(BACKOFFICE_TRANSLATIONS);

  readonly auth = inject(AuthService);
  readonly face = inject(BranchFace);
  /** The user's own photo in the top bar (owner, 2026-11-11); the initials until it arrives, or without one. */
  readonly profile = inject(ProfileService);
  private readonly router = inject(Router);
  private readonly state = inject(BackofficeNavState);
  private readonly confirm = inject(ConfirmService);
  private readonly sync = inject(SyncService);
  private readonly drawer = viewChild.required<ElementRef<HTMLDialogElement>>('drawer');

  readonly appearanceOpen = signal(false);
  readonly sidebarCollapsed = signal(false);

  readonly tone = computed(() => moduleTone(this.state.module()?.key));

  readonly roleLabel = computed(() => {
    const role = this.auth.currentUser()?.role;
    return role ? this.i18n.t('nav.role.' + role.toLowerCase()) : '';
  });

  readonly userInitials = computed(() => {
    const name = this.auth.currentUser()?.fullName?.trim();
    if (!name) return '';
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 0) return '';
    if (parts.length === 1) return Array.from(parts[0])[0].toUpperCase();
    return (Array.from(parts[0])[0] + Array.from(parts[parts.length - 1])[0]).toUpperCase();
  });

  constructor() {
    void this.profile.ensurePhoto();
    // A page reached while the drawer is open (a link inside the page, Back) closes it.
    effect(() => {
      this.state.page();
      const el = this.drawer().nativeElement;
      if (el.open) {
        el.close();
        document.body.classList.remove('drawer-open');
      }
    });
  }

  ngOnDestroy(): void {
    document.body.classList.remove('drawer-open');
  }

  toggleSidebar(): void {
    this.sidebarCollapsed.update((c) => !c);
  }

  openDrawer(): void {
    this.drawer().nativeElement.showModal();
    document.body.classList.add('drawer-open');
  }

  closeDrawer(): void {
    const el = this.drawer().nativeElement;
    if (el.open) el.close();
    document.body.classList.remove('drawer-open');
  }

  onDrawerClick(event: MouseEvent): void {
    if (event.target === this.drawer().nativeElement) this.closeDrawer();
  }

  /** The page in view, so the account screen can come back to it. */
  here(): string {
    return this.router.url;
  }

  async logout(): Promise<void> {
    if (!(await confirmSignOut(this.confirm, this.i18n, this.sync.stats().pending))) return;
    await this.auth.logout();
    await this.router.navigate(['/login']);
  }
}
