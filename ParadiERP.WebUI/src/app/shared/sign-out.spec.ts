import { TestBed } from '@angular/core/testing';
import { I18nService } from '../core/i18n.service';
import { ConfirmRequest, ConfirmService } from '../ui/confirm.service';
import { confirmSignOut } from './sign-out';

describe('confirmSignOut', () => {
  let asked: ConfirmRequest[];
  let answer: boolean;
  let i18n: I18nService;
  let confirm: ConfirmService;

  beforeEach(() => {
    asked = [];
    answer = false;
    TestBed.configureTestingModule({
      providers: [
        { provide: ConfirmService, useValue: { ask: (r: ConfirmRequest) => (asked.push(r), Promise.resolve(answer)) } },
      ],
    });
    i18n = TestBed.inject(I18nService);
    confirm = TestBed.inject(ConfirmService);
  });

  it('asks before signing out, and passes the answer through', async () => {
    expect(await confirmSignOut(confirm, i18n)).toBeFalse();
    answer = true;
    expect(await confirmSignOut(confirm, i18n)).toBeTrue();
    expect(asked.length).toBe(2);
    expect(asked[0].title).toBe(i18n.t('nav.logout.confirmTitle'));
    expect(asked[0].title).not.toBe('nav.logout.confirmTitle');
    expect(asked[0].confirmLabel).toBe(i18n.t('nav.logout'));
  });

  it('names sales still waiting on the device, and only when there are some', async () => {
    await confirmSignOut(confirm, i18n, 0);
    await confirmSignOut(confirm, i18n, 3);
    expect(asked[0].message).not.toContain('3');
    expect(asked[1].message).toContain('3');
  });
});
