import { inject } from '@angular/core';
import { CanActivateFn, RedirectFunction, Route, Router, Routes, UrlMatcher, UrlSegment } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { BACKOFFICE_PAGES, LEGACY_SECTIONS, NavPage, RETIRED_PAGES, canOpen, landingPath, legacyTarget } from '../../core/nav';
import { MaintenanceModule } from '../../core/maintenance-module.service';
import { navUser, storedSubscriptionStatus } from '../../core/subscription-status';
import { BackofficeShellComponent } from './shell/backoffice-shell.component';

/**
 * One back-office page: open only to the users `core/nav.ts` lists it for — the same rule the
 * sidebar draws from, so a typed address and the sidebar never disagree. Refused, the reader lands
 * where they would after signing in. The server still refuses on its own; this only keeps the
 * screen from offering what it would refuse.
 */
export const pageGuard: CanActivateFn = async (route) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const user = auth.currentUser();
  // The company's subscription status decides one page (an owned installation has none to show),
  // read from the device so a typed address agrees with the sidebar from the first navigation.
  // Whether the maintenance module is on decides its job pages (MS-01): asked once, then known.
  const pageKey = route.data['pageKey'] as string;
  const maintenanceOn = pageKey.startsWith('maintenance.') ? await inject(MaintenanceModule).load() : null;
  const reader = navUser(user, await storedSubscriptionStatus(), maintenanceOn);
  return canOpen(reader, pageKey) ? true : router.parseUrl(landingPath(user));
};

const toLanding: RedirectFunction = () => inject(Router).parseUrl(landingPath(inject(AuthService).currentUser()));

/** An old one-segment address (`#/backoffice/<section>`), sent to the page that replaced it, query intact. */
const legacyRedirect =
  (section: string): RedirectFunction =>
  (route) => {
    const target = legacyTarget(section, inject(AuthService).currentUser()) ?? LEGACY_SECTIONS[section];
    return inject(Router).createUrlTree(['/backoffice', ...target.split('/')], { queryParams: route.queryParams });
  };

/** A page that was taken out, sent to the one that took its place, query intact. */
const retiredRedirect =
  (target: string): RedirectFunction =>
  (route) =>
    inject(Router).createUrlTree(['/backoffice', ...target.split('/')], { queryParams: route.queryParams });

/**
 * A deep page's address and everything under it (`rentals/contracts`, `…/c1`, `…/c1/issue`), as
 * one route: Angular then keeps the same page and section while the reader moves between the list,
 * a contract and its documents, so nothing loaded is fetched again, and Back and Forward are just
 * address changes.
 */
function deepMatcher(path: string): UrlMatcher {
  const own = path.split('/');
  return (segments: UrlSegment[]) =>
    segments.length >= own.length && own.every((part, i) => segments[i].path === part)
      ? { consumed: segments }
      : null;
}

function pageRoute(page: NavPage): Route {
  const route: Route = {
    canActivate: [pageGuard],
    data: { pageKey: page.key },
    loadComponent: () => import('./backoffice-page.component').then((m) => m.BackofficePageComponent),
  };
  return page.deep ? { ...route, matcher: deepMatcher(page.path) } : { ...route, path: page.path };
}

/**
 * The back office (REDESIGN_PLAN.md §4): one shell, one child route per page of `core/nav.ts`, and
 * the old addresses redirected. Loaded with the back office, so neither the table nor the shell is
 * in a till's download.
 */
export const BACKOFFICE_ROUTES: Routes = [
  {
    path: '',
    component: BackofficeShellComponent,
    children: [
      { path: '', pathMatch: 'full', redirectTo: toLanding },
      ...BACKOFFICE_PAGES.map(pageRoute),
      ...Object.keys(LEGACY_SECTIONS).map((section) => ({
        path: section,
        pathMatch: 'full' as const,
        redirectTo: legacyRedirect(section),
      })),
      ...Object.entries(RETIRED_PAGES).map(([path, target]) => ({
        path,
        pathMatch: 'full' as const,
        redirectTo: retiredRedirect(target),
      })),
      { path: '**', redirectTo: toLanding },
    ],
  },
];
