import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { AuthService, MIN_PASSWORD_LENGTH } from '../../core/auth.service';
import { ConnectivityService } from '../../core/connectivity.service';
import { I18nService } from '../../core/i18n.service';
import { PosUser } from '../../core/models';
import { ConfirmService } from '../../ui/confirm.service';
import { ChangePasswordComponent, returnPath } from './change-password.component';

describe('returnPath', () => {
  it('goes back to the page the menu was opened from', () => {
    expect(returnPath('/backoffice/admin/users?employee=e2', '/backoffice/dashboard')).toBe('/backoffice/admin/users?employee=e2');
  });

  it('lands where a sign-in would when there is nowhere, or nowhere of ours, to go back to', () => {
    const landing = '/backoffice/dashboard';
    expect(returnPath(null, landing)).toBe(landing);
    expect(returnPath('', landing)).toBe(landing);
    expect(returnPath('https://elsewhere.example/', landing)).toBe(landing);
    expect(returnPath('//elsewhere.example/', landing)).toBe(landing);
    expect(returnPath('/change-password?from=%2Fx', landing)).toBe(landing);
    expect(returnPath('/login', landing)).toBe(landing);
  });
});

describe('ChangePasswordComponent', () => {
  let fixture: ComponentFixture<ChangePasswordComponent>;
  let answer: { ok: boolean; error?: string };
  let sent: [string, string][];
  let navigated: string[];
  let query: Record<string, string>;
  const online = signal(true);
  let probeAnswer: boolean;
  const manager = { id: 'u1', username: '58310427', fullName: 'Nadia Benali', role: 'Manager' } as PosUser;

  const setUp = async () => {
    TestBed.configureTestingModule({
      imports: [ChangePasswordComponent],
      providers: [
        {
          provide: AuthService,
          useValue: {
            currentUser: signal(manager),
            changePassword: (current: string, next: string) => (sent.push([current, next]), Promise.resolve(answer)),
          },
        },
        { provide: ConnectivityService, useValue: { online, probe: () => Promise.resolve(probeAnswer) } },
        { provide: Router, useValue: { navigateByUrl: (url: string) => (navigated.push(url), Promise.resolve(true)) } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    fixture = TestBed.createComponent(ChangePasswordComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  };

  beforeEach(() => {
    answer = { ok: true };
    sent = [];
    navigated = [];
    query = {};
    online.set(true);
    probeAnswer = true;
  });

  const i18n = () => TestBed.inject(I18nService);
  const q = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement | null;
  const text = (selector: string) => q(selector)?.textContent?.trim();

  /** Fills the form and presses Save, then lets the screen redraw. */
  const save = async (current: string, next: string, confirm = next) => {
    const c = fixture.componentInstance;
    c.current = current;
    c.next = next;
    c.confirm = confirm;
    await c.submit();
    fixture.detectChanges();
  };

  it('asks for the current password and the new one twice, and names the account', async () => {
    await setUp();
    expect(q('#cp-current')?.getAttribute('autocomplete')).toBe('current-password');
    expect(q('#cp-new')?.getAttribute('autocomplete')).toBe('new-password');
    expect(q('#cp-confirm')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain(i18n().t('account.password.intro', { name: 'Nadia Benali' }));
    expect(text('#cp-new-hint')).toBe(i18n().t('account.password.newHint', { n: MIN_PASSWORD_LENGTH }));
  });

  it('reads what is typed into the fields', async () => {
    await setUp();
    const type = (id: string, value: string) => {
      const input = q(id) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    type('#cp-current', 'ancien1');
    type('#cp-new', 'nouveau1');
    type('#cp-confirm', 'nouveau1');
    await fixture.componentInstance.submit();
    expect(sent).toEqual([['ancien1', 'nouveau1']]);
  });

  describe('checks before it sends', () => {
    it('says a new password shorter than the users screen’s floor is too short', async () => {
      await setUp();
      await save('ancien1', 'abc');
      expect(text('#cp-new-error')).toBe(i18n().t('backoffice.users.passwordMin', { n: MIN_PASSWORD_LENGTH }));
      expect(q('#cp-new')?.getAttribute('aria-invalid')).toBe('true');
      expect(sent).toEqual([]);
    });

    it('accepts exactly the floor', async () => {
      await setUp();
      await save('ancien1', 'x'.repeat(MIN_PASSWORD_LENGTH));
      expect(q('#cp-new-error')).toBeNull();
      expect(sent.length).toBe(1);
    });

    it('says when the confirmation does not match', async () => {
      await setUp();
      await save('ancien1', 'nouveau1', 'nouveau2');
      expect(text('#cp-confirm-error')).toBe(i18n().t('account.password.error.mismatch'));
      expect(sent).toEqual([]);
    });

    it('says when the new password is the current one', async () => {
      await setUp();
      await save('meme-mot', 'meme-mot');
      expect(text('#cp-new-error')).toBe(i18n().t('account.password.error.same'));
      expect(sent).toEqual([]);
    });

    it('says an empty field is required, and only once it was left or a save was tried', async () => {
      await setUp();
      expect(q('#cp-current-error')).toBeNull();
      fixture.componentInstance.visit('current');
      fixture.detectChanges();
      expect(text('#cp-current-error')).toBe(i18n().t('common.fieldRequired'));
      expect(q('#cp-new-error')).toBeNull();
      await save('', '', '');
      expect(text('#cp-new-error')).toBe(i18n().t('common.fieldRequired'));
      expect(text('#cp-confirm-error')).toBe(i18n().t('common.fieldRequired'));
      expect(sent).toEqual([]);
    });
  });

  describe('the server’s refusals, in words', () => {
    const refused = (code: string) => ({ ok: false, error: JSON.stringify({ status: 400, code, message: 'English for the log' }) });

    it('a wrong current password', async () => {
      await setUp();
      answer = refused('password_wrong');
      await save('ancien1', 'nouveau1');
      expect(text('[data-error]')).toBe(i18n().t('account.password.error.wrong'));
      expect(q('[data-done]')).toBeNull();
    });

    it('a new password too short for the server', async () => {
      await setUp();
      answer = refused('password_too_short');
      await save('ancien1', 'nouveau1');
      expect(text('[data-error]')).toBe(i18n().t('backoffice.users.passwordMin', { n: MIN_PASSWORD_LENGTH }));
    });

    it('a new password the same as the current one', async () => {
      await setUp();
      answer = refused('password_same');
      await save('ancien1', 'nouveau1');
      expect(text('[data-error]')).toBe(i18n().t('account.password.error.same'));
    });

    it('too many guesses at the current password', async () => {
      await setUp();
      answer = { ok: false, error: JSON.stringify({ status: 429, code: 'too_many_attempts', retryAfterSeconds: 60 }) };
      await save('ancien1', 'nouveau1');
      expect(text('[data-error]')).toBe(i18n().t('account.password.error.tooMany'));
    });

    it('a session the server no longer accepts', async () => {
      await setUp();
      answer = { ok: false, error: 'credentials' };
      await save('ancien1', 'nouveau1');
      expect(text('[data-error]')).toBe(i18n().t('account.password.session'));
    });

    it('anything else, without the server’s raw body', async () => {
      await setUp();
      answer = { ok: false, error: '<html>502 Bad Gateway</html>' };
      await save('ancien1', 'nouveau1');
      expect(text('[data-error]')).toBe(i18n().t('account.password.failed'));
    });

    it('in Arabic for an Arabic reader', async () => {
      await setUp();
      const before = i18n().lang();
      i18n().lang.set('ar');
      try {
        answer = refused('password_wrong');
        await save('ancien1', 'nouveau1');
        expect(text('[data-error]')).toBe('كلمة المرور الحالية غير صحيحة.');
      } finally {
        i18n().lang.set(before);
      }
    });
  });

  describe('offline', () => {
    it('says it needs the server, and sends nothing', async () => {
      online.set(false);
      probeAnswer = false;
      await setUp();
      expect(text('[data-offline]')).toBe(i18n().t('account.password.offline'));
      await save('ancien1', 'nouveau1');
      expect(sent).toEqual([]);
      expect(text('[data-error]')).toBe(i18n().t('account.password.offline'));
    });

    it('asks once more before refusing, since the last probe may be stale', async () => {
      online.set(false);
      probeAnswer = true;
      await setUp();
      await save('ancien1', 'nouveau1');
      expect(sent).toEqual([['ancien1', 'nouveau1']]);
    });

    it('says an unreachable server the same way', async () => {
      await setUp();
      answer = { ok: false, error: 'unreachable' };
      await save('ancien1', 'nouveau1');
      expect(text('[data-error]')).toBe(i18n().t('account.password.offline'));
    });
  });

  describe('saved', () => {
    it('says so, forgets what was typed, and goes back to where it was opened from', async () => {
      query = { from: '/backoffice/admin/users' };
      await setUp();
      await save('ancien1', 'nouveau1');
      const c = fixture.componentInstance;
      expect(text('[data-done]')).toBe(i18n().t('account.password.done'));
      expect(q('#cp-new')).toBeNull();
      expect([c.current, c.next, c.confirm]).toEqual(['', '', '']);
      (fixture.nativeElement.querySelector('.submit') as HTMLButtonElement).click();
      expect(navigated).toEqual(['/backoffice/admin/users']);
    });

    it('goes to the landing page when it was opened with nowhere to go back to', async () => {
      await setUp();
      await save('ancien1', 'nouveau1');
      fixture.componentInstance.leave();
      expect(navigated).toEqual(['/backoffice/dashboard']);
    });
  });

  it('Cancel goes back without sending anything', async () => {
    query = { from: '/backoffice/finance/vouchers' };
    await setUp();
    (fixture.nativeElement.querySelector('.actions .btn-ghost') as HTMLButtonElement).click();
    expect(navigated).toEqual(['/backoffice/finance/vouchers']);
    expect(sent).toEqual([]);
  });
});
