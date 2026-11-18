import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { SwUpdate } from '@angular/service-worker';
import { AuthService, offlineTillCode } from '../../core/auth.service';
import { ConnectivityService } from '../../core/connectivity.service';
import { getMeta } from '../../core/db';
import { I18nService } from '../../core/i18n.service';
import { LanguageSwitchService } from '../../core/language-switch.service';
import { DeviceMeta, Lang, PosUser } from '../../core/models';
import { PosService } from '../../core/pos.service';
import { SyncService } from '../../core/sync.service';
import { PasswordVisibilityDirective } from '../../ui/password-visibility.directive';

/**
 * The stable code inside a ProblemDetails body, when the server sent one. The API answers a
 * refused sign-in with `{ ..., "code": "wrong_terminal" }` and the transport hands the body
 * back as the error's message; anything that is not that shape is simply not a code.
 */
function parseProblem(text: string | undefined): { code: string | null; retryAfterSeconds: number | null } | null {
  if (!text) return null;
  try {
    const parsed = JSON.parse(text) as { code?: unknown; retryAfterSeconds?: unknown };
    return {
      code: typeof parsed?.code === 'string' ? parsed.code : null,
      retryAfterSeconds:
        typeof parsed?.retryAfterSeconds === 'number' && Number.isFinite(parsed.retryAfterSeconds)
          ? parsed.retryAfterSeconds
          : null,
    };
  } catch {
    return null;
  }
}

function problemCode(text: string | undefined): string | null {
  return parseProblem(text)?.code ?? null;
}

/**
 * The till code the form offers: the one this browser last signed in with as a till, else nothing.
 * A browser from before `lastTillCode` existed that has synced as a till (a cursor) offers its code;
 * a fresh one offers none — it used to say "C01", which made an office computer the first till.
 */
export function rememberedTillCode(meta: Pick<DeviceMeta, 'lastTillCode' | 'deviceCode' | 'cursor'>): string {
  if (meta.lastTillCode) return meta.lastTillCode;
  return meta.cursor > 0 ? meta.deviceCode : '';
}

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule, PasswordVisibilityDirective],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent implements OnInit {
  readonly i18n = inject(I18nService);
  private readonly switcher = inject(LanguageSwitchService);
  readonly connectivity = inject(ConnectivityService);
  private readonly auth = inject(AuthService);
  private readonly sync = inject(SyncService);
  private readonly pos = inject(PosService);
  private readonly router = inject(Router);
  // Optional: the sign-in screen is also built outside the router (its unit tests).
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly updates = inject(SwUpdate, { optional: true });

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** Arrived here because the back office was left alone too long (IdleLockService). */
  readonly signedOutIdle = signal(false);
  readonly ssoEnabled = signal(false);
  readonly offlineUsers = signal<PosUser[]>([]);

  /** Offline unlock needs a prior sync; without one only the online form is offered. */
  readonly canUnlockOffline = computed(() => this.offlineUsers().length > 0);
  readonly mode = signal<'online' | 'offline'>('online');

  /** The shared counter till's code is asked for only when someone opens this. */
  readonly sharedOpen = signal(false);
  /** The till an offline PIN opens here: the one this computer last signed in on as a till. */
  readonly offlineTill = signal('');

  /**
   * Two-step login (owner, 2026-11-11): once the password was right for an account that has it on,
   * the form asks for the authenticator's code. The password stays in memory only until it is sent again.
   */
  readonly step = signal<'credentials' | 'code'>('credentials');
  twoFactorCode = '';
  /** "Trust this computer for 30 days": off by default, a shared till is never trusted by accident. */
  trustDevice = false;

  username = '';
  password = '';
  pin = '';
  selectedUserId = '';
  /** A shared counter till's code. Empty for everyone else: a cashier opens their own till. */
  sharedCode = '';
  apiBaseUrl = '';

  async ngOnInit(): Promise<void> {
    // The page the visitor opened before being asked to sign in (owner, 2026-10-06: a link to a
    // new contract landed on the dashboard). Kept for the session, so an SSO round trip keeps it.
    rememberReturnUrl(this.route?.snapshot.queryParamMap.get('returnUrl') ?? null);
    this.signedOutIdle.set(this.route?.snapshot.queryParamMap.get('reason') === 'idle');
    const meta = await getMeta();
    this.offlineTill.set(offlineTillCode({ deviceCode: rememberedTillCode(meta), lastTillCode: meta.lastTillCode }));
    this.apiBaseUrl = meta.apiBaseUrl;

    if (this.updates?.isEnabled) {
      this.updates.versionUpdates.subscribe((evt) => {
        if (evt.type === 'VERSION_READY') {
          this.updates?.activateUpdate().then(() => document.location.reload());
        }
      });
      this.updates.checkForUpdate().catch(() => {});
    }

    try {
      const config = await fetch('/api/auth/sso/config', { cache: 'no-store' }).then(r => r.json());
      if (config.isPlatform) { await this.router.navigate(['/platform']); return; }
      this.ssoEnabled.set(config.enabled && (!meta.apiBaseUrl || new URL(meta.apiBaseUrl).origin === window.location.origin));
      const result = new URLSearchParams(window.location.search).get('sso');
      if (result) {
        const url = new URL(window.location.href);
        url.searchParams.delete('sso');
        window.history.replaceState(window.history.state, '', url.href);
      }
      if (result === 'failed') this.error.set(this.i18n.t('login.failed'));
      if (result === 'complete' && this.ssoEnabled()) {
        const signedIn = await this.auth.loginSso();
        if (signedIn.ok) {
          await this.sync.sync('sso-login');
          await this.enter();
          return;
        }
        const code = problemCode(signedIn.error);
        this.error.set(
          code
            ? this.signInError(signedIn.error)
            : (signedIn.error ?? this.i18n.t('login.failed')),
        );
      }
    } catch { /* Offline and legacy installations retain password/PIN login. */ }

    const users = await this.auth.offlineUsers();
    this.offlineUsers.set(users);
    this.selectedUserId = meta.currentUserId ?? users[0]?.id ?? '';

    await this.connectivity.probe();
    if (!this.connectivity.online() && users.length) this.mode.set('offline');
  }

  onLangChange(event: Event): void {
    const target = event.target as HTMLSelectElement;
    const value = target.value as Lang;
    target.value = this.i18n.lang();
    void this.switcher.request(value);
  }

  toggleShared(): void {
    this.sharedOpen.update((open) => !open);
    if (!this.sharedOpen()) this.sharedCode = '';
  }

  /** The code sent online: a shared till's when one was typed, otherwise none. */
  private sharedTill(): string {
    return this.sharedOpen() ? this.sharedCode.trim() : '';
  }

  async startSso(): Promise<void> {
    this.error.set(null);
    try {
      // Read back by the SSO session call after the round trip through the identity provider.
      await this.auth.setDeviceCode(this.sharedTill());
      await this.auth.setApiBaseUrl('');
      window.location.assign('/api/auth/sso/login');
    } catch (error) { this.error.set((error as Error).message); }
  }

  setMode(mode: 'online' | 'offline'): void {
    this.mode.set(mode);
    this.error.set(null);
  }

  /** Back from the code to the name and password; nothing typed for the second step is kept. */
  backToCredentials(): void {
    this.step.set('credentials');
    this.twoFactorCode = '';
    this.password = '';
    this.error.set(null);
  }

  /**
   * Wrong credentials and a switched-off account read the same on purpose (the server keeps
   * them indistinguishable); a terminal refusal is different — the password was right and the
   * cashier has to walk to their own branch's till, so it says so.
   */
  private signInError(error: string | undefined): string {
    if (error === 'unreachable') return this.i18n.t('sync.offlineNotice');
    const problem = parseProblem(error);
    const code = problem?.code;
    if (code === 'wrong_terminal') return this.i18n.t('login.error.wrongTerminal');
    // The password was right; this account works at a till, so the code is what is missing.
    if (code === 'till_required') return this.i18n.t('login.error.tillRequired');
    if (code === 'rateLimited') {
      const seconds = problem?.retryAfterSeconds ?? 60;
      return this.i18n.t('error.rateLimited', { n: seconds });
    }
    if (code === 'too_many_attempts') {
      const seconds = problem?.retryAfterSeconds ?? 60;
      return this.i18n.t('error.tooManyAttempts', { n: seconds });
    }
    if (code === 'two_factor_wrong') return this.i18n.t('login.twoFactor.wrong');
    if (code === 'busy') return this.i18n.t('error.busy');
    if (code === 'credentialsTooLong') return this.i18n.t('error.credentialsTooLong');
    if (code === 'requestTooLarge') return this.i18n.t('error.requestTooLarge');
    return this.i18n.t('login.failed');
  }

  /**
   * Where a signed-in session starts: the till, or — signed in with no till code — the back
   * office, since an office session has nothing to sell against.
   */
  private async enter(): Promise<void> {
    const back = takeReturnUrl();
    if (this.auth.officeSession()) {
      if (back) await this.router.navigateByUrl(back);
      else await this.router.navigate(['/backoffice']);
      return;
    }
    await this.pos.loadCatalog();
    // The till's own guards still apply: a page this session may not open sends it on from there.
    if (back) await this.router.navigateByUrl(back);
    else await this.router.navigate(['/register']);
  }

  async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      if (this.mode() === 'online') {
        await this.auth.setApiBaseUrl(this.apiBaseUrl);
        const second = this.step() === 'code' ? { code: this.twoFactorCode, remember: this.trustDevice } : {};
        const result = await this.auth.loginOnline(this.username.trim(), this.password, this.sharedTill(), second);
        if (!result.ok) {
          if (problemCode(result.error) === 'two_factor_required') {
            // The password was right: now the code. Asked as a step of its own, not as an error.
            this.step.set('code');
            this.twoFactorCode = '';
            setTimeout(() => document.getElementById('two-factor-code')?.focus());
            return;
          }
          this.error.set(this.signInError(result.error));
          if (this.step() === 'code') this.twoFactorCode = '';
          return;
        }
        this.step.set('credentials');
        this.twoFactorCode = '';
        // First sign-in on a terminal pulls the whole catalog before the register opens.
        await this.sync.sync('login');
      } else {
        const result = await this.auth.loginOffline(this.selectedUserId, this.pin);
        if (!result.ok) {
          this.error.set(
            result.error === 'locked'
              ? this.i18n.t('error.tooManyAttempts', { n: result.retryAfterSeconds ?? 60 })
              : this.i18n.t(result.error === 'notill' ? 'login.error.offlineNeedsTill' : 'login.failed'),
          );
          return;
        }
      }

      await this.enter();
    } catch (error) {
      this.error.set((error as Error)?.message ?? this.i18n.t('login.failed'));
    } finally {
      this.busy.set(false);
    }
  }
}

const RETURN_KEY = 'pos.returnUrl';

/**
 * Only a page of this app: a path that starts with one slash, never the sign-in screen itself and
 * never another site (`//host` or a scheme). Anything else is dropped, so a crafted link cannot
 * send a fresh session elsewhere.
 */
export function safeReturnUrl(url: string | null | undefined): string | null {
  if (!url || !url.startsWith('/') || url.startsWith('//') || url.includes('://')) return null;
  if (url === '/' || url.startsWith('/login')) return null;
  return url;
}

function rememberReturnUrl(url: string | null): void {
  const safe = safeReturnUrl(url);
  if (!safe) return;
  try {
    sessionStorage.setItem(RETURN_KEY, safe);
  } catch {
    // Storage refused (a private window): the sign-in simply lands where it always did.
  }
}

function takeReturnUrl(): string | null {
  try {
    const url = sessionStorage.getItem(RETURN_KEY);
    sessionStorage.removeItem(RETURN_KEY);
    return safeReturnUrl(url);
  } catch {
    return null;
  }
}
