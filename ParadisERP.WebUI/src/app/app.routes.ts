import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { AuthService } from './core/auth.service';
import { mayEnterBackOffice } from './core/landing';

/** Sends a signed-out visitor to the sign-in screen with the page they opened, to return to after. */
const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (auth.isAuthenticated()) return true;
  return router.createUrlTree(['/login'], state.url && state.url !== '/' ? { queryParams: { returnUrl: state.url } } : {});
};

/**
 * The till's pages need a till: an office session (signed in with no till code, 2026-10-05) has
 * no receipt numbers, branch stock or cash box to sell against, so it is shown why instead.
 */
export const tillGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.officeSession() ? router.createUrlTree(['/office']) : true;
};

// The back office edits the catalog directly on the server (see AdminService), so it is
// restricted to whoever works off the till — a cashier only ever reads it. A cashier who holds the
// cash box comes in too, and the back office shows them its receipts and vouchers only. Read from the
// keys the login reported, or, on a session from before them, from the role (core/landing.ts).
const backofficeGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return mayEnterBackOffice(auth.currentUser()) ? true : router.createUrlTree(['/register']);
};

/**
 * Root guard:
 * - On client workspaces (*.erp.paradistack.com except erp.paradistack.com), clients have NO landing page:
 *   redirects directly to '/login' (or '/register' if authenticated).
 * - On the general system (erp.paradistack.com or localhost), shows the LandingComponent.
 */
const rootGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const host = typeof window !== 'undefined' ? window.location.hostname.toLowerCase() : '';
  const isClientWorkspace = host.endsWith('.erp.paradistack.com') && host !== 'erp.paradistack.com';
  if (isClientWorkspace) {
    return auth.isAuthenticated() ? router.createUrlTree(['/register']) : router.createUrlTree(['/login']);
  }
  return true;
};

export const routes: Routes = [
  { path: 'landing', loadComponent: () => import('./features/landing/landing.component').then(m => m.LandingComponent) },
  { path: 'platform', loadComponent: () => import('./features/platform/platform.component').then(m => m.PlatformComponent) },
  {
    path: '',
    pathMatch: 'full',
    canActivate: [rootGuard],
    loadComponent: () => import('./features/landing/landing.component').then(m => m.LandingComponent),
  },
  {
    path: 'login',
    loadComponent: () => import('./features/login/login.component').then((m) => m.LoginComponent),
  },
  // The till: unchanged addresses, so its offline entry points and redirects stay as they were.
  {
    path: 'register',
    canActivate: [authGuard, tillGuard],
    loadComponent: () =>
      import('./features/register/register.component').then((m) => m.RegisterComponent),
  },
  {
    path: 'products',
    canActivate: [authGuard, tillGuard],
    loadComponent: () =>
      import('./features/products/products.component').then((m) => m.ProductsComponent),
  },
  {
    path: 'sales',
    canActivate: [authGuard, tillGuard],
    loadComponent: () => import('./features/sales/sales.component').then((m) => m.SalesComponent),
  },
  {
    path: 'sync',
    canActivate: [authGuard, tillGuard],
    loadComponent: () =>
      import('./features/sync/sync-status.component').then((m) => m.SyncStatusComponent),
  },
  // An office session's answer to a till address: this computer is not a till, and how to make it one.
  {
    path: 'office',
    canActivate: [authGuard],
    loadComponent: () => import('./features/office/not-a-till.component').then((m) => m.NotATillComponent),
  },
  // A signed-in user's own password (owner, 2026-10-05). Its own page, drawn as the sign-in card —
  // outside the back-office shell and the till, which AppComponent's `layoutFor` leaves bare.
  // "My profile" (owner, 2026-11-11): the signed-in user's own details, photo, password and two-step
  // login. Outside the shell like the change of password, so a cashier reaches it too.
  {
    path: 'account',
    canActivate: [authGuard],
    loadComponent: () => import('./features/account/profile.component').then((m) => m.ProfileComponent),
  },
  {
    path: 'change-password',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/account/change-password.component').then((m) => m.ChangePasswordComponent),
  },
  // The back office (REDESIGN_PLAN.md §4): its shell, its pages and the old addresses, in its own
  // chunk (features/backoffice/backoffice.routes.ts).
  {
    path: 'backoffice',
    canActivate: [authGuard, backofficeGuard],
    loadChildren: () => import('./features/backoffice/backoffice.routes').then((m) => m.BACKOFFICE_ROUTES),
  },
  // One document, in its own tab, without the app chrome: what the Print buttons open and what
  // a server-side renderer would drive later (docs/07 §4.2). Query: lang, copies, auto=1.
  // AppComponent draws no header for this prefix — see `layoutFor` there.
  {
    path: 'print/:kind/:id',
    canActivate: [authGuard, backofficeGuard],
    loadComponent: () => import('./features/print/print-page.component').then((m) => m.PrintPageComponent),
  },
  { path: '**', redirectTo: '' },
];
