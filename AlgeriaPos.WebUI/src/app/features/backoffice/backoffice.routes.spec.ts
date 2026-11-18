import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { patchMeta } from '../../core/db';
import { PosUser, SubscriptionAccess } from '../../core/models';
import { pageGuard } from './backoffice.routes';

/** A typed address agrees with the sidebar: an owned installation has no subscription page to open. */
describe('back-office page guard and the subscription page', () => {
  const admin = { id: 'u1', username: 'admin', fullName: 'Admin', role: 'Admin', permissions: [] } as unknown as PosUser;
  const lease = (status: SubscriptionAccess['status']): SubscriptionAccess => ({
    plan: 'Standard', status, canOperate: true, accessUntilUtc: null,
    maxUsers: null, maxBranches: null, maxDevices: null, checkedAtUtc: '2026-10-05T00:00:00Z',
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: { currentUser: signal(admin) } }],
    });
  });

  afterEach(async () => {
    await patchMeta({ subscription: undefined });
  });

  async function open(pageKey: string): Promise<true | string> {
    const route = { data: { pageKey } } as unknown as ActivatedRouteSnapshot;
    const result = await TestBed.runInInjectionContext(() => pageGuard(route, {} as RouterStateSnapshot));
    if (result === true) return true;
    return TestBed.inject(Router).serializeUrl(result as UrlTree);
  }

  it('sends an Unrestricted company from the subscription address to the dashboard', async () => {
    await patchMeta({ subscription: lease('Unrestricted') });
    expect(await open('admin.subscription')).toBe('/backoffice/dashboard');
    // The rest of the administration opens as before.
    expect(await open('admin.company')).toBeTrue();
  });

  it('opens the subscription page for a hosted company', async () => {
    await patchMeta({ subscription: lease('Active') });
    expect(await open('admin.subscription')).toBeTrue();
    await patchMeta({ subscription: lease('Trial') });
    expect(await open('admin.subscription')).toBeTrue();
  });

  it('opens it while the device has no status yet, as before', async () => {
    await patchMeta({ subscription: undefined });
    expect(await open('admin.subscription')).toBeTrue();
  });
});
