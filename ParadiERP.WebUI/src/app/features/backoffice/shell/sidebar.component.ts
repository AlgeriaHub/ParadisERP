import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  booleanAttribute,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../core/auth.service';
import { BranchFace } from '../../../core/branch-face';
import { I18nService } from '../../../core/i18n.service';
import { MaintenanceModule } from '../../../core/maintenance-module.service';
import { NavModule, NavPage, visibleNav } from '../../../core/nav';
import { ToneClass, moduleTone } from '../../../core/nav-tones';
import { SubscriptionStatus, navUser } from '../../../core/subscription-status';
import { IconComponent } from '../../../ui/icon.component';
import { BackofficeNavState } from './backoffice-nav-state';

let nextId = 0;

/**
 * The back-office menu (owner, 2026-11-12: "text beside the icon; the toggle arrow shows only the
 * icons"). One list: each module is its icon, in its section tone, and its name. A module of
 * several pages opens in place and lists them on a tree line (one module open at a time, the
 * current page's to begin with); a module of one page is that page's link.
 *
 * - The arrow on the menu's inner edge folds it to the icons alone. Folded — and always between 768
 *   and 1000px — a module's icon floats its pages beside the icons until a page is chosen, a click
 *   outside or Escape; the names stay as tooltips and accessible labels.
 * - In the phone drawer the menu is always open, with the working branch above the list.
 * - Drawn from `core/nav.ts` for the user signed in; every page is a real `<a routerLink>` with
 *   `aria-current="page"` when it is the open one.
 */
@Component({
  selector: 'app-bo-sidebar',
  standalone: true,
  imports: [RouterLink, IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.is-collapsed]': 'collapsed()',
    '[class.is-drawer]': 'drawer()',
  },
  template: `
    <div class="menu">
      <div class="menu__head">
        <div class="brand" [attr.title]="face.name() || i18n.t('app.title')">
          @if (face.logo(); as logo) {
            <img class="brand__logo" [src]="logo" alt="" />
          } @else {
            <span class="brand__mark" aria-hidden="true">{{ i18n.countryCode() }}</span>
          }
          <span class="brand__text label">
            <strong dir="auto">{{ face.name() || i18n.t('app.title') }}</strong>
            <small>{{ i18n.t('backoffice.title') }}</small>
          </span>
        </div>
        @if (drawer()) {
          <button type="button" class="menu__close" [attr.aria-label]="i18n.t('common.close')" (click)="closeRequested.emit()">
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true">
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        }
      </div>

      @if (!drawer()) {
        <!-- The fold: on the menu's inner edge, pointing the way the menu will go. -->
        <button
          type="button"
          class="menu__toggle"
          (click)="toggleRequested.emit()"
          [attr.aria-expanded]="!collapsed()"
          [attr.aria-label]="collapsed() ? i18n.t('nav.expand') : i18n.t('nav.collapse')"
          [attr.title]="collapsed() ? i18n.t('nav.expand') : i18n.t('nav.collapse')"
        >
          <svg class="menu__toggle-icon" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.25" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M15 6l-6 6 6 6" />
          </svg>
        </button>
      }

      <div class="menu__top"><ng-content select="[menuTop]" /></div>

      <nav class="menu__nav" [attr.aria-label]="i18n.t('nav.main')">
        <ul class="mods">
          @for (m of modules(); track m.key) {
            <li class="mod" [class]="tone(m)" [class.is-current]="m.key === currentModule()">
              @if (single(m); as page) {
                <a
                  class="mod__row"
                  [routerLink]="link(page)"
                  [attr.aria-current]="page.key === currentPage() ? 'page' : null"
                  [attr.title]="i18n.t(m.label)"
                  (click)="pick(page)"
                >
                  <span class="mod__icon"><app-icon [name]="m.icon" /></span>
                  <span class="mod__label label">{{ i18n.t(m.label) }}</span>
                </a>
              } @else {
                <button
                  type="button"
                  class="mod__row"
                  [attr.aria-expanded]="isOpen(m)"
                  [attr.aria-controls]="subId(m)"
                  [attr.title]="i18n.t(m.label)"
                  (click)="toggleModule(m)"
                >
                  <span class="mod__icon"><app-icon [name]="m.icon" /></span>
                  <span class="mod__label label">{{ i18n.t(m.label) }}</span>
                  <svg class="mod__chevron label" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                    <path d="M15 6l-6 6 6 6" />
                  </svg>
                </button>
                @if (isOpen(m)) {
                  <div class="sub" [id]="subId(m)" [class.is-floating]="iconsOnly()">
                    @if (iconsOnly()) {
                      <p class="sub__title">{{ i18n.t(m.label) }}</p>
                    }
                    <ul class="sub__list">
                      @for (p of m.pages; track p.key) {
                        <li>
                          <a
                            class="sub__link"
                            [routerLink]="link(p)"
                            [attr.aria-current]="p.key === currentPage() ? 'page' : null"
                            (click)="pick(p)"
                          >
                            <span class="sub__dot" aria-hidden="true"></span>
                            <span class="sub__label">{{ i18n.t(p.label) }}</span>
                          </a>
                        </li>
                      }
                    </ul>
                  </div>
                }
              }
            </li>
          }
        </ul>
      </nav>
    </div>
  `,
  styleUrl: './sidebar.component.scss',
})
export class SidebarComponent {
  readonly i18n = inject(I18nService);
  readonly face = inject(BranchFace);
  private readonly auth = inject(AuthService);
  private readonly state = inject(BackofficeNavState);
  private readonly subscription = inject(SubscriptionStatus);
  /** MS-01: the maintenance module's job pages show only while it is on. */
  private readonly maintenance = inject(MaintenanceModule);
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly collapsed = input(false);
  readonly drawer = input(false, { transform: booleanAttribute });
  /** A page was chosen: the drawer closes on it. */
  readonly navigated = output<void>();
  /** The drawer's × was pressed. */
  readonly closeRequested = output<void>();
  /** The fold arrow was pressed: the shell keeps the folded state. */
  readonly toggleRequested = output<void>();

  private readonly idPrefix = `bo-nav-${nextId++}`;

  readonly modules = computed<NavModule[]>(() =>
    visibleNav(navUser(this.auth.currentUser(), this.subscription.status(), this.maintenance.on())),
  );
  readonly currentPage = computed(() => this.state.page()?.key ?? null);
  readonly currentModule = computed(() => this.state.module()?.key ?? null);

  /** Between 768 and 1000px the menu is icons only whatever was chosen (it has no room for names). */
  private readonly narrow = signal(typeof matchMedia === 'function' && matchMedia('(min-width: 768px) and (max-width: 999.98px)').matches);

  /** Icons only: folded by the arrow or narrow — never in the drawer. */
  readonly iconsOnly = computed(() => !this.drawer() && (this.collapsed() || this.narrow()));

  /**
   * The module listed open. Open: the reader's choice, else the current page's module. Icons only:
   * only what the reader floated, nothing by default (a floating list over the page is asked for).
   */
  private readonly chosen = signal<string | null | undefined>(undefined);
  readonly openKey = computed<string | null>(() => {
    const chosen = this.chosen();
    if (chosen !== undefined) return chosen;
    return this.iconsOnly() ? null : this.currentModule();
  });

  constructor() {
    // A new page, or a fold or unfold, starts again from the current page's module.
    effect(() => {
      this.currentModule();
      this.iconsOnly();
      this.chosen.set(undefined);
    });
    if (typeof matchMedia === 'function') {
      const query = matchMedia('(min-width: 768px) and (max-width: 999.98px)');
      query.addEventListener?.('change', (e) => this.narrow.set(e.matches));
    }
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    const target = event.target as Node | null;
    if (this.iconsOnly() && (!target || !this.host.nativeElement.contains(target))) this.chosen.set(null);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.iconsOnly()) this.chosen.set(null);
  }

  single(m: NavModule): NavPage | null {
    return m.pages.length === 1 ? m.pages[0] : null;
  }

  tone(m: NavModule): ToneClass {
    return moduleTone(m.key);
  }

  isOpen(m: NavModule): boolean {
    return this.openKey() === m.key;
  }

  subId(m: NavModule): string {
    return `${this.idPrefix}-${m.key}`;
  }

  /** Opens a module's pages, closing the one that was open; pressing the open one closes it. */
  toggleModule(m: NavModule): void {
    this.chosen.set(this.isOpen(m) ? null : m.key);
  }

  link(p: NavPage): string {
    return p.external ? p.path : '/backoffice/' + p.path;
  }

  pick(p: NavPage): void {
    this.state.picked(p);
    if (this.iconsOnly()) this.chosen.set(null);
    this.navigated.emit();
  }
}
