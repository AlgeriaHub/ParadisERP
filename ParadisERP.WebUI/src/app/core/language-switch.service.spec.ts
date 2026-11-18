import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { I18nService } from './i18n.service';
import { LanguageSwitchService } from './language-switch.service';
import { ConfirmRequest, ConfirmService } from '../ui/confirm.service';

describe('LanguageSwitchService', () => {
  let switcher: LanguageSwitchService;
  let i18n: I18nService;
  let asked: ConfirmRequest[];
  let confirmAnswer: boolean;

  let stored: string | null;
  let html: { lang: string; dir: string };
  const doc = document as unknown as { startViewTransition?: (cb: () => void) => unknown };
  let originalViewTransition: unknown;

  beforeEach(() => {
    stored = localStorage.getItem('pos.lang');
    html = { lang: document.documentElement.lang, dir: document.documentElement.dir };
    localStorage.removeItem('pos.lang');

    originalViewTransition = doc.startViewTransition;
    doc.startViewTransition = (cb: () => void) => {
      cb();
      return {};
    };

    asked = [];
    confirmAnswer = false;

    TestBed.configureTestingModule({
      providers: [
        {
          provide: ConfirmService,
          useValue: {
            ask: (request: ConfirmRequest) => {
              asked.push(request);
              return Promise.resolve(confirmAnswer);
            },
          },
        },
      ],
    });

    switcher = TestBed.inject(LanguageSwitchService);
    i18n = TestBed.inject(I18nService);
  });

  afterEach(() => {
    doc.startViewTransition = originalViewTransition as typeof doc.startViewTransition;
    if (stored === null) localStorage.removeItem('pos.lang');
    else localStorage.setItem('pos.lang', stored);
    document.documentElement.lang = html.lang;
    document.documentElement.dir = html.dir;
  });

  it('cancelled confirm leaves the language', fakeAsync(() => {
    confirmAnswer = false;
    const startLang = i18n.lang();
    const targetLang = startLang === 'ar' ? 'fr' : 'ar';

    let resolved: boolean | undefined;
    switcher.request(targetLang).then((res) => (resolved = res));
    tick();

    expect(resolved).toBeFalse();
    expect(i18n.lang()).toBe(startLang);
    expect(switcher.pending()).toBeNull();
    expect(switcher.secondsLeft()).toBe(0);
    expect(asked.length).toBe(1);
    expect(asked[0].title).toBe(i18n.t('lang.confirm.title'));
    expect(asked[0].confirmLabel).toBe(i18n.t('lang.confirm.ok'));
    expect(asked[0].focusConfirm).toBeTrue();
  }));

  it('confirmed → language unchanged after 4 s, changed after 5 s', fakeAsync(() => {
    confirmAnswer = true;
    const startLang = i18n.lang();
    const targetLang = startLang === 'ar' ? 'fr' : 'ar';

    let resolved: boolean | undefined;
    switcher.request(targetLang).then((res) => (resolved = res));
    tick(0);

    expect(switcher.pending()).toBe(targetLang);
    expect(switcher.secondsLeft()).toBe(5);

    tick(4000);
    expect(i18n.lang()).toBe(startLang);
    expect(switcher.pending()).toBe(targetLang);
    expect(switcher.secondsLeft()).toBe(1);

    tick(1000);
    expect(i18n.lang()).toBe(targetLang);
    expect(switcher.pending()).toBeNull();
    expect(switcher.secondsLeft()).toBe(0);
    expect(resolved).toBeTrue();
  }));

  it('same language → no ask', fakeAsync(() => {
    let resolved: boolean | undefined;
    switcher.request(i18n.lang()).then((res) => (resolved = res));
    tick();

    expect(resolved).toBeFalse();
    expect(asked.length).toBe(0);
    expect(switcher.pending()).toBeNull();
  }));

  it('returns false and does not ask if a switch is already pending', fakeAsync(() => {
    confirmAnswer = true;
    const startLang = i18n.lang();
    const targetLang = startLang === 'ar' ? 'fr' : 'ar';

    switcher.request(targetLang);
    tick(0);
    expect(switcher.pending()).toBe(targetLang);

    let secondResult: boolean | undefined;
    switcher.request('en').then((res) => (secondResult = res));
    tick(0);

    expect(secondResult).toBeFalse();
    expect(asked.length).toBe(1);

    tick(5000);
    expect(i18n.lang()).toBe(targetLang);
  }));
});
