import { Injectable, computed, signal } from '@angular/core';
import * as bcrypt from 'bcryptjs';
import { ApiService } from './api.service';
import { ConnectivityService } from './connectivity.service';
import { LanguagePackService } from './language-pack.service';
import { IntegrationsService } from './integrations.service';
import { RegionalService } from './regional.service';
import { db, getMeta, patchMeta } from './db';
import { DeviceMeta, LoginResponse, PosUser } from './models';
import { sellsUntaxedFrom } from './untaxed-sale';

/**
 * The branch the last online sign-in gave this user, laid over the stored row — a pull rewrites a
 * cashier's row without it. Another user, or no online sign-in yet, is left as stored. Whether they may
 * sell untaxed is laid over for every user an online sign-in here has answered for (untaxedByUser).
 */
export function withBranch(user: PosUser, meta: Pick<DeviceMeta, 'sessionBranch' | 'untaxedByUser'>): PosUser {
  const untaxed = meta.untaxedByUser?.[user.id];
  const known = untaxed === undefined ? user : { ...user, sellsUntaxed: untaxed };
  const session = meta.sessionBranch;
  if (!session || session.userId !== user.id) return known;
  return {
    ...known,
    branchId: session.branchId,
    branchLocked: session.locked,
    permissions: session.permissions ?? [],
    capabilities: session.capabilities ?? [],
    // Laid over only when the session stored them: an older one leaves them absent, read as "decide by the role".
    ...(session.permissionKeys
      ? { permissionKeys: session.permissionKeys, roleId: session.roleId ?? null, roleName: session.roleName ?? null, scope: session.scope ?? null }
      : {}),
  };
}

/**
 * The role and the keys a sign-in answered (AUTH-1 Step 6), when the server sent them; an older server's
 * answer adds nothing, and the screens go on reading the role.
 */
function accessOf(user: LoginResponse['user']): Pick<PosUser, 'permissionKeys' | 'roleId' | 'roleName' | 'scope'> {
  return user.permissionKeys
    ? { permissionKeys: user.permissionKeys, roleId: user.roleId ?? null, roleName: user.roleName ?? null, scope: user.scope ?? null }
    : {};
}

/**
 * The fewest characters a password may have: the server's floor, stated in the field rather than
 * discovered on refusal. One figure for the users screen, which sets a password, and the account
 * screen, where the user changes their own.
 */
export const MIN_PASSWORD_LENGTH = 6;

/**
 * The till an offline PIN opens: the current one, else the last this browser signed in on as a
 * till. Empty when this computer has never been a till.
 */
export function offlineTillCode(meta: Pick<DeviceMeta, 'deviceCode' | 'lastTillCode'>): string {
  return meta.deviceCode || meta.lastTillCode || '';
}

/**
 * The receipt counter after an online sign-in. On the till this browser already numbered for, it
 * goes past what the server has seen (another computer may have sold on the same till — a
 * cashier's own till opened on a second PC); on a different till it starts after that till's
 * highest number. An office session, with no till, leaves the counter alone.
 */
export function nextLocalSeq(
  meta: Pick<DeviceMeta, 'deviceCode' | 'lastTillCode' | 'localSeq'>,
  response: Pick<LoginResponse, 'deviceCode' | 'lastLocalSeq'>,
): number {
  if (!response.deviceCode) return meta.localSeq;
  const server = response.lastLocalSeq ?? 0;
  // The same till as last time, even with an office session in between (current code empty):
  // never go back below this browser's own numbers, some may still be waiting to be sent.
  const sameTill = response.deviceCode === offlineTillCode(meta);
  return sameTill ? Math.max(meta.localSeq, server) : server;
}

/** Wrong PINs before a cashier's offline unlock closes, and for how long (security review, 2026-10-10). */
const MAX_PIN_FAILURES = 5;
const PIN_LOCK_MS = 60_000;
/** How long a sign-out waits for the server to end the session before going ahead anyway. */
const LOGOUT_WAIT_MS = 3_000;

/**
 * Two ways in.
 *
 * Online: username + password against the API, which returns a long-lived JWT (30 days) so
 * the terminal keeps a usable token through a long outage.
 *
 * Offline: the cashier's numeric PIN, checked against the BCrypt hash replicated with the
 * user record. The trade-off is deliberate — an offline register has to authenticate
 * someone with no server, and a per-user BCrypt hash is far better than a shared password
 * in localStorage. Web/admin password hashes are never replicated.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly currentUser = signal<PosUser | null>(null);
  readonly isAuthenticated = computed(() => this.currentUser() !== null);
  readonly hasCatalog = signal(false);
  /**
   * Signed in with no till code: the back office from a computer that is not a till (owner,
   * 2026-10-05). The server allows it to roles that read beyond a till's branch only, and such a
   * session cannot record a sale, so the till's pages explain instead of opening.
   */
  readonly officeSession = signal(false);

  constructor(
    private api: ApiService,
    private connectivity: ConnectivityService,
    private regional: RegionalService,
    private languagePacks: LanguagePackService,
    private integrations: IntegrationsService,
  ) {}

  /** Called once at start-up: restores the session and configures the API base URL. */
  async restore(): Promise<void> {
    const meta = await getMeta();
    this.regional.set(meta.regional);
    this.integrations.set(meta.integrations);
    await this.languagePacks.loadCached();
    this.connectivity.configure(meta.apiBaseUrl);
    this.hasCatalog.set((await db.products.count()) > 0);

    if (meta.currentUserId) {
      const user = await db.users.get(meta.currentUserId);
      if (user && user.isActive) {
        this.currentUser.set(withBranch(user, meta));
        this.officeSession.set(!meta.deviceCode);
      }
    }
  }

  async setApiBaseUrl(url: string): Promise<void> {
    const clean = url.trim().replace(/\/+$/, '');
    const meta = await getMeta();
    const currentOrigin = new URL(meta.apiBaseUrl || window.location.origin).origin;
    const nextOrigin = new URL(clean || window.location.origin).origin;
    if (currentOrigin !== nextOrigin && ((await db.sales.count()) > 0 || (await db.products.count()) > 0 || meta.token))
      throw new Error('Open the other company in its own browser address. This terminal already contains company data.');
    await patchMeta({ apiBaseUrl: clean });
    this.connectivity.configure(clean);
  }

  /** Empty is meaningful: an office sign-in, with no till. It no longer falls back to "C01". */
  async setDeviceCode(code: string): Promise<void> {
    await patchMeta({ deviceCode: (code ?? '').trim().toUpperCase().slice(0, 8) });
  }

  /**
   * Online sign-in. No till code by default (owner, 2026-10-05: "no need to sign in with a till
   * code except offline"): the server puts a cashier on their own till and opens the back office
   * for everyone else. `sharedTill` is the code of a shared counter till, typed only for one.
   */
  async loginOnline(
    username: string,
    password: string,
    sharedTill = '',
    second: { code?: string; remember?: boolean } = {},
  ): Promise<{ ok: boolean; error?: string }> {
    // Two-step login: a computer this account trusted earlier sends its token instead of a code.
    const key = username.trim().toLowerCase();
    const trusted = (await getMeta()).twoFactorTrust?.[key];
    const code = second.code?.trim();
    const result = await this.signIn(
      () =>
        this.api.login(username, password, sharedTill.trim().toUpperCase().slice(0, 8), {
          ...(code ? { twoFactorCode: code, rememberDevice: second.remember === true } : {}),
          ...(trusted ? { trustToken: trusted } : {}),
        }),
      async (response) => {
        // Remembered when asked; a code typed without "trust" forgets a token the server no longer took.
        if (response.twoFactorTrust) await this.rememberTrust(key, response.twoFactorTrust);
        else if (code && trusted) await this.rememberTrust(key, null);
      },
    );
    return result;
  }

  private async rememberTrust(key: string, token: string | null): Promise<void> {
    const all = { ...((await getMeta()).twoFactorTrust ?? {}) };
    if (token) all[key] = token;
    else delete all[key];
    await patchMeta({ twoFactorTrust: all });
  }

  async loginSso(): Promise<{ ok: boolean; error?: string }> {
    return this.signIn(meta => this.api.getJson<LoginResponse>(`/api/auth/sso/session?deviceCode=${encodeURIComponent(meta.deviceCode)}`));
  }

  /**
   * The signed-in user chooses their own password (owner, 2026-10-05: "the user after login can
   * change password"). Saving it revokes the session in hand and the server answers a fresh one,
   * so that answer is stored exactly as a sign-in stores it; a refusal changes nothing here and
   * comes back as the server's body, its `code` naming why.
   */
  async changePassword(currentPassword: string, newPassword: string): Promise<{ ok: boolean; error?: string }> {
    return this.signIn(() => this.api.changePassword(currentPassword, newPassword));
  }

  private async signIn(
    load: (meta: DeviceMeta) => Promise<LoginResponse>,
    after?: (response: LoginResponse) => Promise<void>,
  ): Promise<{ ok: boolean; error?: string }> {
    const meta = await getMeta();
    try {
      const response = await load(meta);
      await after?.(response);
      const sellsUntaxed = sellsUntaxedFrom(response.user.capabilities);
      await patchMeta({
        token: response.token,
        tokenExpiresAtUtc: response.expiresAtUtc,
        currentUserId: response.user.id,
        deviceCode: response.deviceCode ?? '',
        localSeq: nextLocalSeq(meta, response),
        // Only a till sign-in is remembered for the form: an office one leaves the last till's code.
        ...(response.deviceCode ? { lastTillCode: response.deviceCode } : {}),
        deviceBranchId: response.deviceBranchId ?? null,
        // A session stored before the branch lock existed has no sessionBranch and reads as not
        // locked until the next online sign-in. Acceptable: the server takes the branch of a
        // non-admin's documents and lists from their user row whatever the screen sent.
        sessionBranch: {
          userId: response.user.id,
          branchId: response.user.branchId ?? null,
          locked: response.user.branchLocked === true,
          permissions: response.user.permissions ?? [],
          capabilities: response.user.capabilities ?? [],
          ...accessOf(response.user),
        },
        // Per user, so a PIN unlock later — offline, after a pull rewrote the row — still knows.
        ...(sellsUntaxed === undefined ? {} : { untaxedByUser: { ...(meta.untaxedByUser ?? {}), [response.user.id]: sellsUntaxed } }),
        ...(response.subscription ? { subscription: response.subscription } : {}),
        ...(response.regional ? { regional: response.regional } : {}),
        ...(response.integrations ? { integrations: response.integrations } : {}),
      });
      this.regional.set(response.regional);
      this.integrations.set(response.integrations);
      await this.languagePacks.sync(response.languagePacks);

      // The user row itself arrives with the first catalog pull; seed a placeholder so the
      // shell can render immediately.
      const existing = await db.users.get(response.user.id);
      const user: PosUser = {
        id: response.user.id,
        username: response.user.username,
        fullName: response.user.fullName,
        role: response.user.role as PosUser['role'],
        pinHash: response.user.role === 'Cashier' ? (existing?.pinHash ?? '') : '',
        isActive: true,
        version: existing?.version ?? 0,
        branchId: response.user.branchId ?? null,
        branchLocked: response.user.branchLocked === true,
        permissions: response.user.permissions ?? [],
        capabilities: response.user.capabilities ?? [],
        ...accessOf(response.user),
        ...(sellsUntaxed === undefined ? {} : { sellsUntaxed }),
      };
      await db.users.put(user);
      this.currentUser.set(user);
      this.officeSession.set(!response.deviceCode);
      return { ok: true };
    } catch (error) {
      const status = (error as { status?: number })?.status;
      // A 401 is a wrong password, said the same for every account — except the second step of a
      // two-step login, whose body names it (two_factor_required / two_factor_wrong).
      if (status === 401) {
        const body = (error as Error)?.message ?? '';
        return { ok: false, error: /"code"\s*:\s*"two_factor_/.test(body) ? body : 'credentials' };
      }
      if (status === 0) return { ok: false, error: 'unreachable' };
      return { ok: false, error: (error as Error)?.message ?? 'unknown' };
    }
  }

  /**
   * Offline unlock. Requires that this terminal has synced at least once. A PIN opens a till
   * (cashiers only): the one this browser last signed in on as a till, never a typed code — the
   * code is the one thing a cashier no longer has to know (owner, 2026-10-05).
   */
  async loginOffline(userId: string, pin: string): Promise<{ ok: boolean; error?: string; retryAfterSeconds?: number }> {
    const till = offlineTillCode(await getMeta());
    if (!till) return { ok: false, error: 'notill' };
    const user = await db.users.get(userId);
    if (!user || !user.isActive || user.role !== 'Cashier') return { ok: false, error: 'credentials' };
    if (!user.pinHash) return { ok: false, error: 'nopin' };

    // Five wrong PINs close this cashier's offline unlock for a minute (security review, 2026-10-10).
    const meta = await getMeta();
    const lock = meta.pinLock?.[user.id];
    if (lock?.untilUtc && new Date(lock.untilUtc).getTime() > Date.now()) {
      const seconds = Math.ceil((new Date(lock.untilUtc).getTime() - Date.now()) / 1000);
      return { ok: false, error: 'locked', retryAfterSeconds: seconds };
    }

    let matches = false;
    try {
      matches = bcrypt.compareSync(pin, user.pinHash);
    } catch {
      matches = false;
    }
    if (!matches) {
      const failures = (lock?.failures ?? 0) + 1;
      const untilUtc = failures >= MAX_PIN_FAILURES ? new Date(Date.now() + PIN_LOCK_MS).toISOString() : null;
      await patchMeta({ pinLock: { ...(meta.pinLock ?? {}), [user.id]: { failures: untilUtc ? 0 : failures, untilUtc } } });
      return { ok: false, error: 'credentials' };
    }

    // The server session on this till belongs to whoever last signed in online. Unlocking as
    // someone else drops it (security review, 2026-10-10): every call would otherwise go out as
    // that other cashier, with their rights and under their name. Sales made now wait in the
    // outbox until this cashier signs in online; nothing is lost.
    const tokenOwner = meta.sessionBranch?.userId ?? null;
    const otherLocks = Object.fromEntries(Object.entries(meta.pinLock ?? {}).filter(([id]) => id !== user.id));
    // After an office session the current code is empty; the till comes back from memory.
    await patchMeta({
      currentUserId: user.id,
      deviceCode: till,
      pinLock: otherLocks,
      ...(tokenOwner && tokenOwner !== user.id ? { token: null, tokenExpiresAtUtc: null } : {}),
    });
    this.currentUser.set(withBranch(user, await getMeta()));
    this.officeSession.set(false);
    return { ok: true };
  }

  /** Users this terminal can unlock offline. */
  async offlineUsers(): Promise<PosUser[]> {
    const users = await db.users.toArray();
    return users.filter((u) => u.isActive && u.role === 'Cashier' && !!u.pinHash);
  }

  /**
   * Cashier tokens remain available for outbox sync; privileged tokens are removed so
   * an admin sign-out cannot leave administrative API access behind on a shared till.
   */
  async logout(): Promise<void> {
    const privileged = this.currentUser()?.role !== 'Cashier';
    // The session ends on the server too, not only here (security review, 2026-10-10): a copy
    // of the token taken from this computer would otherwise stay good until it expired. Best
    // effort and brief — offline, or with the server slow, signing out never waits on it. A
    // cashier's token stays on the till for the offline PIN, so it is not ended.
    if (privileged && (await getMeta()).token && this.connectivity.online()) {
      await Promise.race([
        Promise.resolve()
          .then(() => this.api.postJson('/api/auth/logout', {}))
          .catch(() => undefined),
        new Promise((resolve) => setTimeout(resolve, LOGOUT_WAIT_MS)),
      ]);
    }
    await patchMeta({ currentUserId: null, token: null, tokenExpiresAtUtc: null });
    this.currentUser.set(null);
    this.officeSession.set(false);
  }
}
