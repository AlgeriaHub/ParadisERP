import { TestBed } from '@angular/core/testing';
import { ApiError, ApiService } from './api.service';
import { AuthService } from './auth.service';
import { db, getMeta, patchMeta } from './db';
import { DeviceMeta, LoginResponse, PosUser } from './models';

/**
 * Changing one's own password (owner, 2026-10-05). The server revokes the session the change was
 * sent with and answers a fresh one in the sign-in's shape, so the device must end up holding
 * exactly what a sign-in with that answer would have stored — and, refused, exactly what it held.
 */
describe('AuthService.changePassword — the session swap', () => {
  let sent: unknown[][];
  let answer: () => Promise<LoginResponse>;

  const fresh: LoginResponse = {
    token: 'new-token',
    expiresAtUtc: '2026-11-04T08:00:00Z',
    deviceCode: 'C02',
    lastLocalSeq: 10,
    deviceBranchId: 'alger',
    user: {
      id: 'u1',
      username: '58310427',
      fullName: 'Nadia Benali',
      role: 'Cashier',
      branchId: 'alger',
      branchLocked: true,
      permissions: ['treasury.cash_box'],
      capabilities: [],
    },
  };

  const stored: PosUser = {
    id: 'u1', username: '58310427', fullName: 'Nadia', role: 'Cashier', pinHash: '$2a$10$hash', isActive: true, version: 3,
  } as PosUser;

  /** A till session as a sign-in yesterday left it. */
  const seed = async () => {
    await db.meta.clear();
    await db.users.clear();
    await patchMeta({
      token: 'old-token',
      tokenExpiresAtUtc: '2026-10-05T08:00:00Z',
      currentUserId: 'u1',
      deviceCode: 'C02',
      lastTillCode: 'C02',
      localSeq: 12,
      sessionBranch: { userId: 'u1', branchId: 'alger', locked: true, permissions: [], capabilities: [] },
    });
    await db.users.put(stored);
  };

  /** What matters of the stored session, without the sync bookkeeping a sign-in leaves alone. */
  const session = (meta: DeviceMeta) => ({
    token: meta.token,
    tokenExpiresAtUtc: meta.tokenExpiresAtUtc,
    currentUserId: meta.currentUserId,
    deviceCode: meta.deviceCode,
    lastTillCode: meta.lastTillCode,
    localSeq: meta.localSeq,
    deviceBranchId: meta.deviceBranchId,
    sessionBranch: meta.sessionBranch,
  });

  beforeEach(async () => {
    sent = [];
    answer = () => Promise.resolve(fresh);
    TestBed.configureTestingModule({
      providers: [
        {
          provide: ApiService,
          useValue: {
            changePassword: (...args: unknown[]) => (sent.push(['password', ...args]), answer()),
            login: (...args: unknown[]) => (sent.push(['login', ...args]), answer()),
            getJson: () => Promise.reject(new ApiError('not in this test', 404)),
          },
        },
      ],
    });
    await seed();
  });

  afterEach(async () => {
    await db.meta.clear();
    await db.users.clear();
  });

  it('sends the current and the new password, and nothing else', async () => {
    const auth = TestBed.inject(AuthService);
    await auth.changePassword('ancien1', 'nouveau1');
    expect(sent).toEqual([['password', 'ancien1', 'nouveau1']]);
  });

  it('replaces the stored session with the one the server answered', async () => {
    const auth = TestBed.inject(AuthService);
    expect(await auth.changePassword('ancien1', 'nouveau1')).toEqual({ ok: true });

    const meta = await getMeta();
    expect(meta.token).toBe('new-token');
    expect(meta.tokenExpiresAtUtc).toBe('2026-11-04T08:00:00Z');
    expect(meta.sessionBranch?.permissions).toEqual(['treasury.cash_box']);
    // Still the same till: its receipt counter never goes back below this browser's own numbers.
    expect(meta.deviceCode).toBe('C02');
    expect(meta.localSeq).toBe(12);
    expect(auth.currentUser()?.fullName).toBe('Nadia Benali');
    expect(auth.officeSession()).toBeFalse();
    // The cashier's offline PIN is the user row's own and survives the swap untouched.
    expect((await db.users.get('u1'))?.pinHash).toBe('$2a$10$hash');
  });

  it('stores exactly what a sign-in with the same answer stores', async () => {
    const auth = TestBed.inject(AuthService);
    await auth.changePassword('ancien1', 'nouveau1');
    const afterChange = session(await getMeta());
    const userAfterChange = await db.users.get('u1');

    await seed();
    await auth.loginOnline('58310427', 'nouveau1');
    expect(session(await getMeta())).toEqual(afterChange);
    expect(await db.users.get('u1')).toEqual(userAfterChange);
  });

  it('refused, keeps the session it had and hands the reason back', async () => {
    const body = JSON.stringify({ status: 400, code: 'password_wrong' });
    answer = () => Promise.reject(new ApiError(body, 400));
    const auth = TestBed.inject(AuthService);
    const before = session(await getMeta());

    expect(await auth.changePassword('faux', 'nouveau1')).toEqual({ ok: false, error: body });
    expect(session(await getMeta())).toEqual(before);
    expect((await getMeta()).token).toBe('old-token');
    expect(await db.users.get('u1')).toEqual(stored);
  });

  it('says an expired session and an unreachable server apart', async () => {
    const auth = TestBed.inject(AuthService);
    answer = () => Promise.reject(new ApiError('', 401));
    expect(await auth.changePassword('ancien1', 'nouveau1')).toEqual({ ok: false, error: 'credentials' });
    answer = () => Promise.reject(new ApiError('Network error', 0));
    expect(await auth.changePassword('ancien1', 'nouveau1')).toEqual({ ok: false, error: 'unreachable' });
    expect((await getMeta()).token).toBe('old-token');
  });
});
