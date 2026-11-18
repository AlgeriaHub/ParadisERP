import { TestBed } from '@angular/core/testing';
import * as bcrypt from 'bcryptjs';
import { ApiError, ApiService } from './api.service';
import { AuthService } from './auth.service';
import { db, getMeta, patchMeta } from './db';
import { PosUser } from './models';

/**
 * The offline PIN unlock (security review, 2026-10-10): five wrong PINs close a cashier's unlock
 * for a minute, and unlocking as a cashier other than the one the server session belongs to drops
 * that session — every call would otherwise go out under the other cashier's name and rights.
 */
describe('AuthService.loginOffline — the PIN lock and the session owner', () => {
  const cashier = (id: string, pin: string): PosUser =>
    ({ id, username: id, fullName: id, role: 'Cashier', pinHash: bcrypt.hashSync(pin, 4), isActive: true, version: 1 }) as PosUser;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [{ provide: ApiService, useValue: { getJson: () => Promise.reject(new ApiError('not in this test', 404)) } }],
    });
    await db.meta.clear();
    await db.users.clear();
    await patchMeta({
      token: 'nadia-token',
      tokenExpiresAtUtc: '2026-11-04T08:00:00Z',
      deviceCode: 'C02',
      lastTillCode: 'C02',
      sessionBranch: { userId: 'nadia', branchId: 'alger', locked: true, permissions: [], capabilities: [] },
    });
    await db.users.bulkPut([cashier('nadia', '1234'), cashier('karim', '5678')]);
  });

  afterEach(async () => {
    await db.meta.clear();
    await db.users.clear();
  });

  it('closes the unlock after five wrong PINs, even to the right one', async () => {
    const auth = TestBed.inject(AuthService);
    for (let i = 0; i < 5; i++) expect((await auth.loginOffline('nadia', '0000')).error).toBe('credentials');

    const locked = await auth.loginOffline('nadia', '1234');
    expect(locked.ok).toBeFalse();
    expect(locked.error).toBe('locked');
    expect(locked.retryAfterSeconds).toBeGreaterThan(0);
    // Another cashier of the same till is not locked out by it.
    expect((await auth.loginOffline('karim', '5678')).ok).toBeTrue();
  });

  it('forgets the wrong PINs once the right one is typed', async () => {
    const auth = TestBed.inject(AuthService);
    for (let i = 0; i < 4; i++) await auth.loginOffline('nadia', '0000');
    expect((await auth.loginOffline('nadia', '1234')).ok).toBeTrue();
    expect((await getMeta()).pinLock?.['nadia']).toBeUndefined();
  });

  it('keeps the server session for its own cashier and drops it for another', async () => {
    const auth = TestBed.inject(AuthService);
    expect((await auth.loginOffline('nadia', '1234')).ok).toBeTrue();
    expect((await getMeta()).token).toBe('nadia-token');

    expect((await auth.loginOffline('karim', '5678')).ok).toBeTrue();
    expect((await getMeta()).token).toBeNull();
    expect(auth.currentUser()?.id).toBe('karim');
  });
});
