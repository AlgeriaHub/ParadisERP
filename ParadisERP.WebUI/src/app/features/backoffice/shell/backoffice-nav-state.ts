import { Injectable, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { BACKOFFICE_PAGES, NavPage, moduleOf } from '../../../core/nav';

/** The page a back-office address belongs to: its own page, or the page an entity sits under. */
export function pageForUrl(url: string): NavPage | undefined {
  const path = url.split(/[?#]/)[0].replace(/^\/backoffice\/?/, '');
  return BACKOFFICE_PAGES.find((p) => path === p.path || path.startsWith(p.path + '/'));
}

/**
 * What the shell knows about where the reader is: the open page, its module, and a counter
 * bumped when the reader picks the page they are already on.
 *
 * The router ignores a link to the address it is already showing, but two sections have always
 * read that click as "back to the list": customer accounts (to the board) and payment vouchers.
 * `reselected` keeps that behaviour, which the old rail delivered through its `reset` inputs.
 */
@Injectable({ providedIn: 'root' })
export class BackofficeNavState {
  private readonly router = inject(Router);

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );

  readonly page = computed(() => pageForUrl(this.url() ?? ''));
  readonly module = computed(() => {
    const page = this.page();
    return page ? moduleOf(page.key) : undefined;
  });

  private readonly picks = signal<{ key: string; n: number }>({ key: '', n: 0 });

  /** How many times the reader re-picked this page while on it. */
  reselected(key: string): number {
    const p = this.picks();
    return p.key === key ? p.n : 0;
  }

  /** Called by the sidebar on every pick; counts only a pick of the page already open. */
  picked(page: NavPage): void {
    if (this.page()?.key !== page.key) {
      this.picks.set({ key: page.key, n: 0 });
      return;
    }
    this.picks.update((p) => ({ key: page.key, n: p.key === page.key ? p.n + 1 : 1 }));
  }
}
