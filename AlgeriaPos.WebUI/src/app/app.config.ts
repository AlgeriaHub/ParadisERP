import {
  APP_INITIALIZER,
  ApplicationConfig,
  ErrorHandler,
  inject,
  isDevMode,
  provideAppInitializer,
  provideZoneChangeDetection,
} from '@angular/core';
import { Router, provideRouter, withHashLocation } from '@angular/router';
import * as Sentry from '@sentry/angular';
import { provideServiceWorker } from '@angular/service-worker';
import { DOCUMENT } from '@angular/common';

import { routes } from './app.routes';
import { setErrorTranslator } from './core/error-message';
import { AuthService } from './core/auth.service';
import { installLatinNumberInputs } from './core/latin-number-inputs';
import { I18nService } from './core/i18n.service';
import { SyncService } from './core/sync.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    // Hash routing so the app also works when opened from a file share or a kiosk
    // wrapper that has no server-side rewrite rules.
    provideRouter(routes, withHashLocation()),
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      // Register immediately rather than after 30s of stability: a shop terminal is often
      // closed and reopened quickly, and the app shell must be cached on the first visit.
      registrationStrategy: 'registerWhenStable:5000',
    }),
    // Errors Angular catches go to Sentry (core/monitoring.ts) as well as the console, and route
    // changes become spans. Both do nothing where monitoring is off (development, the tests).
    { provide: ErrorHandler, useValue: Sentry.createErrorHandler() },
    { provide: Sentry.TraceService, deps: [Router] },
    provideAppInitializer(() => {
      inject(Sentry.TraceService);
    }),
    provideAppInitializer(() => {
      const i18n = inject(I18nService);
      setErrorTranslator((key) => i18n.t(key));
    }),
    // Number boxes in Latin digits on a browser whose language writes others (core/latin-number-inputs.ts).
    provideAppInitializer(() => {
      installLatinNumberInputs(inject(DOCUMENT));
    }),
    {
      provide: APP_INITIALIZER,
      multi: true,
      deps: [AuthService, SyncService],
      useFactory: (auth: AuthService, sync: SyncService) => async () => {
        await auth.restore();
        sync.startAutoSync();
      },
    },
  ],
};
