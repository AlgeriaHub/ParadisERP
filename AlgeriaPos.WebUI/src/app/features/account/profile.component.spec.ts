import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { ConnectivityService } from '../../core/connectivity.service';
import { I18nService } from '../../core/i18n.service';
import { Profile, ProfileService, photoLink } from '../../core/profile.service';
import { ConfirmService } from '../../ui/confirm.service';
import { ProfileComponent, qrPath } from './profile.component';

const PROFILE: Profile = {
  id: 'u1',
  fullName: 'Amina Benali',
  username: '48213907',
  loginId: '48213907',
  role: 'Manager',
  roleName: 'Manager',
  branchNameAr: 'وكالة وهران',
  branchNameFr: 'Agence Oran',
  branchNameEn: 'Oran branch',
  employee: { number: 'EMP-000012', jobTitle: 'Gérante', phone: '0555 12 34 56', email: 'amina@example.dz', hiredOn: '2024-03-01' },
  photoVersion: null,
  twoFactor: { enabled: false, enabledAtUtc: null, recoveryCodesLeft: 0 },
};

describe('qrPath', () => {
  it('draws the setup address as one path of dark modules, nothing else', () => {
    const { size, d } = qrPath('otpauth://totp/Shop:48213907?secret=JBSWY3DPEHPK3PXP&issuer=Shop');
    expect(size).toBeGreaterThanOrEqual(21);
    expect(d).toMatch(/^(M\d+ \d+h1v1h-1z)+$/);
  });
});

describe('photoLink', () => {
  it('is versioned, and absent without a photo', () => {
    expect(photoLink('ab12')).toBe('/api/auth/profile/photo?v=ab12');
    expect(photoLink(null)).toBeNull();
  });
});

describe('ProfileComponent', () => {
  let fixture: ComponentFixture<ProfileComponent>;
  let calls: string[];

  beforeEach(async () => {
    calls = [];
    const photo = signal<string | null>(null);
    TestBed.configureTestingModule({
      imports: [ProfileComponent],
      providers: [
        provideRouter([]),
        {
          provide: ProfileService,
          useValue: {
            photo,
            load: () => Promise.resolve(structuredClone(PROFILE)),
            showPhoto: () => Promise.resolve(),
            setup: (password: string) => (
              calls.push('setup:' + password),
              Promise.resolve({
                secret: 'JBSW Y3DP EHPK 3PXP',
                provisioningUri: 'otpauth://totp/Shop:48213907?secret=JBSWY3DPEHPK3PXP&issuer=Shop',
                issuer: 'Shop',
                account: '48213907',
              })
            ),
            enable: (code: string) => (calls.push('enable:' + code), Promise.resolve({ codes: ['abcde-fghjk', 'mnpqr-stuvw'] })),
            disable: () => Promise.resolve({ enabled: false, enabledAtUtc: null, recoveryCodesLeft: 0 }),
            newRecoveryCodes: () => Promise.resolve({ codes: [] }),
          },
        },
        { provide: AuthService, useValue: { currentUser: signal({ id: 'u1', role: 'Manager' }) } },
        { provide: ConnectivityService, useValue: { online: signal(true), probe: () => Promise.resolve(true) } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).lang.set('fr');
    spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
    fixture = TestBed.createComponent(ProfileComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  const q = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLElement;

  it('says who the account is, read-only, with the branch in the reader\'s language', () => {
    expect(q('#profile-name').textContent?.trim()).toBe('Amina Benali');
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Agence Oran');
    expect(text).toContain('EMP-000012');
    expect(text).toContain('01/03/2024');
    expect(fixture.nativeElement.querySelectorAll('.facts input').length).toBe(0);
  });

  it('turns two-step login on: password, then the key as a QR code and text, then a code, then the recovery codes once', async () => {
    const i18n = TestBed.inject(I18nService);
    const component = fixture.componentInstance;
    expect(q('[data-two-factor] app-status').textContent?.trim()).toBe(i18n.t('account.twoFactor.off'));

    component.ask('setup');
    component.password = 'Essai@2026';
    await component.confirmPassword();
    fixture.detectChanges();
    expect(calls).toEqual(['setup:Essai@2026']);
    expect(q('svg.qr path').getAttribute('d')).toMatch(/^M/);
    expect(q('[data-secret]').textContent?.trim()).toBe('JBSW Y3DP EHPK 3PXP');

    component.code = '123456';
    await component.enable();
    fixture.detectChanges();
    expect(calls).toEqual(['setup:Essai@2026', 'enable:123456']);
    expect(q('[data-recovery-codes]').textContent).toContain('abcde-fghjk');
    expect(q('[data-two-factor] app-status').textContent?.trim()).toBe(i18n.t('account.twoFactor.on'));

    // Shown once: leaving the step forgets them.
    component.doneWithCodes();
    fixture.detectChanges();
    expect(q('[data-recovery-codes]')).toBeNull();
    expect(component.codes()).toEqual([]);
  });

  it('says a wrong password in words and stays on the password step', async () => {
    const service = TestBed.inject(ProfileService) as unknown as { setup: () => Promise<never> };
    service.setup = () => Promise.reject(Object.assign(new Error(JSON.stringify({ code: 'password_wrong' })), { status: 400 }));
    const component = fixture.componentInstance;
    component.ask('setup');
    component.password = 'pas-le-bon';
    await component.confirmPassword();
    fixture.detectChanges();
    expect(q('[data-error]').textContent?.trim()).toBe(TestBed.inject(I18nService).t('account.password.error.wrong'));
    expect(component.step()).toBe('password');
  });
});
