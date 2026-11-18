import { SelectSearchComponent } from './ui/select-search.component';
import { Component, DestroyRef, ElementRef, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { AuthService } from './core/auth.service';
import { I18nService } from './core/i18n.service';
import { IdleLockService } from './core/idle-lock.service';
import { UiFontService } from './core/ui-font.service';
import { ThemeService } from './core/theme.service';
import { TillHeaderComponent } from './shared/till-header.component';
import { UpdateBannerComponent } from './shared/update-banner.component';
import { LanguageSwitchOverlayComponent } from './shared/language-switch-overlay.component';

/**
 * Which chrome a route gets (REDESIGN_PLAN.md §7.1):
 *
 * - `till` — `/register`, `/sales`, `/products`, `/sync`: the till's own header, no sidebar;
 * - `backoffice` — `/backoffice/**`: the back-office shell draws its sidebar and header itself;
 * - `print` — `/print/**`: the document and nothing else, not even the update banner;
 * - `bare` — the login and the operator console.
 */
export type Layout = 'till' | 'backoffice' | 'print' | 'landing' | 'bare';

export function layoutFor(url: string): Layout {
  const path = url.split(/[?#]/)[0];
  if (path === '/') return 'landing';
  if (/^\/print(\/|$)/.test(path)) return 'print';
  if (/^\/backoffice(\/|$)/.test(path)) return 'backoffice';
  if (/^\/(register|sales|products|sync)(\/|$)/.test(path)) return 'till';
  return 'bare';
}

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [SelectSearchComponent, RouterOutlet, TillHeaderComponent, UpdateBannerComponent, LanguageSwitchOverlayComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent {
  constructor() {
    const host = inject(ElementRef<HTMLElement>).nativeElement;
    const revealInvalid = (event: Event) => {
      const target = event.target;
      if (!(target instanceof HTMLElement)) return;
      for (let parent = target.parentElement; parent; parent = parent.parentElement) {
        if (parent instanceof HTMLDetailsElement) parent.open = true;
      }
    };
    host.addEventListener('invalid', revealInvalid, true);
    inject(DestroyRef).onDestroy(() => host.removeEventListener('invalid', revealInvalid, true));
    // The back office signs itself out when left alone (security review, 2026-10-10).
    inject(IdleLockService).start();
  }
  readonly i18n = inject(I18nService);
  readonly auth = inject(AuthService);
  // Injected for its effect: it applies the company's chosen interface face.
  private readonly uiFont = inject(UiFontService);
  /** Started at boot, so the appearance follows the device's own light or dark while set to "system". */
  private readonly theme = inject(ThemeService);
  private readonly router = inject(Router);

  /**
   * Read from the router URL rather than from route data, because the header is rendered here,
   * above the outlet, before any route component exists.
   */
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );

  readonly layout = computed(() => layoutFor(this.url() ?? ''));
}
