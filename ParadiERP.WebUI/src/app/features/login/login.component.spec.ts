import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { ConnectivityService } from '../../core/connectivity.service';
import { db } from '../../core/db';
import { I18nService } from '../../core/i18n.service';
import { PosService } from '../../core/pos.service';
import { SyncService } from '../../core/sync.service';
import { LoginComponent, rememberedTillCode, safeReturnUrl } from './login.component';

describe('rememberedTillCode', () => {
  it('offers nothing on a fresh browser — no more "C01" by default', () => {
    expect(rememberedTillCode({ deviceCode: '', cursor: 0 })).toBe('');
  });

  it('offers the code this browser last signed in with as a till', () => {
    expect(rememberedTillCode({ lastTillCode: 'C03', deviceCode: '', cursor: 12 })).toBe('C03');
  });

  it('keeps the last till code after an office session', () => {
    // An office sign-in stores an empty deviceCode and leaves lastTillCode alone.
    expect(rememberedTillCode({ lastTillCode: 'C02', deviceCode: '', cursor: 40 })).toBe('C02');
  });

  it('reads a till from before the field existed by its sync cursor', () => {
    expect(rememberedTillCode({ deviceCode: 'C01', cursor: 7 })).toBe('C01');
    // Stored by the old default but never synced as a till: not offered.
    expect(rememberedTillCode({ deviceCode: 'C01', cursor: 0 })).toBe('');
  });
});

describe('LoginComponent — no till code online', () => {
  let fixture: ComponentFixture<LoginComponent>;
  let loginResult: { ok: boolean; error?: string };
  let navigated: unknown[][];
  let sentTill: string[];
  let sentSecond: unknown[];
  const officeSession = signal(false);

  beforeEach(async () => {
    await db.meta.clear();
    loginResult = { ok: true };
    navigated = [];
    sentTill = [];
    sentSecond = [];
    officeSession.set(false);
    // No server in a unit test: the SSO probe reads an empty answer (SSO off, not the operator console).
    spyOn(window, 'fetch').and.callFake(() => Promise.resolve(new Response('{}')));

    TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        {
          provide: AuthService,
          useValue: {
            officeSession,
            setDeviceCode: () => Promise.resolve(),
            setApiBaseUrl: () => Promise.resolve(),
            offlineUsers: () => Promise.resolve([]),
            loginOnline: (_u: string, _p: string, till = '', second: unknown = {}) => (
              sentTill.push(till), sentSecond.push(second), Promise.resolve(loginResult)
            ),
            loginOffline: () => Promise.resolve({ ok: false }),
            loginSso: () => Promise.resolve({ ok: false }),
          },
        },
        { provide: ConnectivityService, useValue: { online: signal(true), probe: () => Promise.resolve(true) } },
        { provide: SyncService, useValue: { sync: () => Promise.resolve({ ok: true }) } },
        { provide: PosService, useValue: { loadCatalog: () => Promise.resolve() } },
        { provide: Router, useValue: { navigate: (cmd: unknown[]) => (navigated.push(cmd), Promise.resolve(true)) } },
      ],
    });
    TestBed.inject(I18nService);
    fixture = TestBed.createComponent(LoginComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  const q = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement;

  it('asks for no till code online, and sends none', async () => {
    expect(q('#device')).toBeNull();
    expect(q('.shared-toggle').getAttribute('aria-expanded')).toBe('false');
    await fixture.componentInstance.submit();
    expect(sentTill).toEqual(['']);
    expect(navigated).toEqual([['/register']]);
  });

  it('opens a shared counter till by its code, behind the toggle, and says what it is for', async () => {
    q('.shared-toggle').click();
    fixture.detectChanges();
    expect(q('.shared-toggle').getAttribute('aria-expanded')).toBe('true');
    expect(q('#device-hint').textContent?.trim()).toBe(TestBed.inject(I18nService).t('login.sharedTillHint'));
    fixture.componentInstance.sharedCode = ' comptoir ';
    await fixture.componentInstance.submit();
    expect(sentTill).toEqual(['comptoir']);
  });

  it('forgets a shared code once the toggle is closed again', async () => {
    fixture.componentInstance.toggleShared();
    fixture.componentInstance.sharedCode = 'C09';
    fixture.componentInstance.toggleShared();
    await fixture.componentInstance.submit();
    expect(sentTill).toEqual(['']);
  });

  it('opens the back office after an office sign-in', async () => {
    officeSession.set(true);
    await fixture.componentInstance.submit();
    expect(navigated).toEqual([['/backoffice']]);
  });

  it('tells an account with no till of its own, in words, what to do', async () => {
    loginResult = { ok: false, error: JSON.stringify({ status: 403, code: 'till_required' }) };
    await fixture.componentInstance.submit();
    fixture.detectChanges();
    expect(q('.error').textContent?.trim()).toBe(TestBed.inject(I18nService).t('login.error.tillRequired'));
    expect(navigated).toEqual([]);
  });

  it('shows a rate-limit notice instead of login.failed when rateLimited is returned', async () => {
    const i18n = TestBed.inject(I18nService);
    loginResult = { ok: false, error: JSON.stringify({ status: 429, code: 'rateLimited', retryAfterSeconds: 30 }) };
    await fixture.componentInstance.submit();
    fixture.detectChanges();
    const errorText = q('.error').textContent?.trim();
    expect(errorText).toBe(i18n.t('error.rateLimited', { n: 30 }));
    expect(errorText).not.toBe(i18n.t('login.failed'));
    expect(navigated).toEqual([]);
  });

  it('still shows login.failed for wrong credentials', async () => {
    const i18n = TestBed.inject(I18nService);
    loginResult = { ok: false, error: 'credentials' };
    await fixture.componentInstance.submit();
    fixture.detectChanges();
    expect(q('.error').textContent?.trim()).toBe(i18n.t('login.failed'));
  });

  it('asks for the authenticator code as a step of its own once the password was right, then sends it', async () => {
    const i18n = TestBed.inject(I18nService);
    loginResult = { ok: false, error: JSON.stringify({ status: 401, code: 'two_factor_required' }) };
    await fixture.componentInstance.submit();
    fixture.detectChanges();
    expect(fixture.componentInstance.step()).toBe('code');
    expect(q('#two-factor-code')).not.toBeNull();
    expect(q('#password')).toBeNull();
    // Asked, not refused: no error is shown.
    expect(q('.error')).toBeNull();
    expect(navigated).toEqual([]);

    // A wrong code says so in words and keeps the code step.
    loginResult = { ok: false, error: JSON.stringify({ status: 401, code: 'two_factor_wrong' }) };
    fixture.componentInstance.twoFactorCode = '000000';
    await fixture.componentInstance.submit();
    fixture.detectChanges();
    expect(q('.error').textContent?.trim()).toBe(i18n.t('login.twoFactor.wrong'));
    expect(fixture.componentInstance.step()).toBe('code');

    loginResult = { ok: true };
    fixture.componentInstance.twoFactorCode = '123456';
    fixture.componentInstance.trustDevice = true;
    await fixture.componentInstance.submit();
    expect(sentSecond.at(-1)).toEqual({ code: '123456', remember: true });
    expect(navigated).toEqual([['/register']]);
  });

  it('goes back from the code to the name and password, forgetting what was typed', async () => {
    loginResult = { ok: false, error: JSON.stringify({ status: 401, code: 'two_factor_required' }) };
    fixture.componentInstance.password = 'secret';
    await fixture.componentInstance.submit();
    fixture.componentInstance.twoFactorCode = '12';
    fixture.componentInstance.backToCredentials();
    fixture.detectChanges();
    expect(fixture.componentInstance.step()).toBe('credentials');
    expect(fixture.componentInstance.password).toBe('');
    expect(fixture.componentInstance.twoFactorCode).toBe('');
    expect(q('#password')).not.toBeNull();
  });

  it('offline, names the till this computer last opened, or says to sign in online once', async () => {
    const i18n = TestBed.inject(I18nService);
    fixture.componentInstance.setMode('offline');
    fixture.detectChanges();
    expect(q('.error').textContent?.trim()).toBe(i18n.t('login.error.offlineNeedsTill'));

    fixture.componentInstance.offlineTill.set('C04');
    fixture.detectChanges();
    expect(q('.till-note').textContent?.trim()).toBe(i18n.t('login.offlineTill', { code: 'C04' }));
    expect(q('#device')).toBeNull();
  });
});

describe('safeReturnUrl', () => {
  it('returns to a page of this app', () => {
    expect(safeReturnUrl('/backoffice/rentals/contracts/new')).toBe('/backoffice/rentals/contracts/new');
  });
  it('never to another site, the sign-in screen or nothing at all', () => {
    expect(safeReturnUrl('//evil.example')).toBeNull();
    expect(safeReturnUrl('https://evil.example')).toBeNull();
    expect(safeReturnUrl('/login?returnUrl=/x')).toBeNull();
    expect(safeReturnUrl('/')).toBeNull();
    expect(safeReturnUrl(null)).toBeNull();
  });
});
