import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { AuthService } from './auth.service';
import { IDLE_LOCK_MS, IdleLockService, watchedByIdleLock } from './idle-lock.service';

/** The back office signs itself out when left alone; the till never does (security review, 2026-10-10). */
describe('IdleLockService', () => {
  let url: string;
  let signedOut: number;
  let navigated: unknown[][];

  beforeEach(() => {
    url = '/backoffice/dashboard';
    signedOut = 0;
    navigated = [];
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { currentUser: () => ({ id: 'u1' }), logout: () => (signedOut++, Promise.resolve()) } },
        {
          provide: Router,
          useValue: {
            get url() { return url; },
            navigate: (cmd: unknown[], extras: unknown) => (navigated.push([cmd, extras]), Promise.resolve(true)),
          },
        },
      ],
    });
  });

  it('watches the back office and never the till', () => {
    expect(watchedByIdleLock('/backoffice')).toBeTrue();
    expect(watchedByIdleLock('/backoffice/receivables?tab=receipts')).toBeTrue();
    expect(watchedByIdleLock('/office')).toBeTrue();
    expect(watchedByIdleLock('/register')).toBeFalse();
    expect(watchedByIdleLock('/sales')).toBeFalse();
    expect(watchedByIdleLock('/print/invoice/1')).toBeFalse();
  });

  it('signs out after the idle time on a back-office page, and says why on the sign-in screen', async () => {
    const lock = TestBed.inject(IdleLockService);
    expect(await lock.check(Date.now() + IDLE_LOCK_MS - 60_000)).toBeFalse();
    expect(await lock.check(Date.now() + IDLE_LOCK_MS + 1_000)).toBeTrue();
    expect(signedOut).toBe(1);
    expect(navigated).toEqual([[['/login'], { queryParams: { reason: 'idle' } }]]);
  });

  it('leaves the register alone however long it waits', async () => {
    url = '/register';
    const lock = TestBed.inject(IdleLockService);
    expect(await lock.check(Date.now() + 10 * IDLE_LOCK_MS)).toBeFalse();
    expect(signedOut).toBe(0);
  });
});
