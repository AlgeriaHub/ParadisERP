import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { tillGuard } from './app.routes';
import { AuthService } from './core/auth.service';

describe('tillGuard', () => {
  const officeSession = signal(false);

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: { officeSession } }],
    });
  });

  const run = () =>
    TestBed.runInInjectionContext(() => tillGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));

  it('opens the till pages in a till session', () => {
    officeSession.set(false);
    expect(run()).toBeTrue();
  });

  it('sends an office session to the page that explains it is not a till', () => {
    officeSession.set(true);
    const result = run();
    expect(result instanceof UrlTree).toBeTrue();
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/office');
  });
});
