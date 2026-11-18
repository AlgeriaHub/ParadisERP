import { TestBed } from '@angular/core/testing';
import { I18nService } from './i18n.service';

/**
 * A language chosen and then un-chosen before its cross-fade runs. The browser defers the
 * change to a view-transition callback, so for a moment the screen still shows the old
 * language; choosing that one again must win, not be dropped as "already showing".
 */
describe('I18nService.setLang', () => {
  const doc = document as unknown as { startViewTransition?: unknown };
  let original: unknown;
  let pending: (() => void)[];
  let stored: string | null;
  let html: { lang: string; dir: string };

  beforeEach(() => {
    // The choice is remembered on the device and on <html>; put both back afterwards.
    stored = localStorage.getItem('pos.lang');
    html = { lang: document.documentElement.lang, dir: document.documentElement.dir };
    localStorage.removeItem('pos.lang');
    pending = [];
    original = doc.startViewTransition;
    // Defer every update, as a real view transition does, until the test flushes them.
    doc.startViewTransition = (update: () => void) => {
      pending.push(update);
      return {};
    };
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    doc.startViewTransition = original;
    if (stored === null) localStorage.removeItem('pos.lang');
    else localStorage.setItem('pos.lang', stored);
    document.documentElement.lang = html.lang;
    document.documentElement.dir = html.dir;
  });

  const flush = () => pending.splice(0).forEach((update) => update());

  it('keeps the last choice when a language is chosen and then un-chosen before the switch runs', () => {
    const i18n = TestBed.inject(I18nService);
    const start = i18n.lang();
    const other = start === 'ar' ? 'fr' : 'ar';

    i18n.setLang(other);
    i18n.setLang(start);
    flush();

    expect(i18n.lang()).toBe(start);
    expect(document.documentElement.dir).toBe(start === 'ar' ? 'rtl' : 'ltr');
  });

  it('applies the latest of several quick choices once', () => {
    const i18n = TestBed.inject(I18nService);
    const start = i18n.lang();
    const other = start === 'ar' ? 'fr' : 'ar';

    i18n.setLang(other);
    i18n.setLang('en');
    flush();

    expect(i18n.lang()).toBe('en');
    i18n.setLang(other);
    flush();
    expect(i18n.lang()).toBe(other);
  });
});

/**
 * Dates and times are shown on the company's clock (Algiers in the default profile), whatever the
 * browser's. Times read back from the database used to arrive without their Z and were taken for
 * the browser's own clock: a dispatch at 23:12 in Algiers read 20:12 on a computer in Riyadh
 * (2026-10-13), and one just after midnight fell on the day before.
 */
describe('I18nService dates', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it('shows a UTC moment at its hour in the company zone', () => {
    const text = TestBed.inject(I18nService).dateTimeIn('fr', '2026-10-13T22:12:00Z');
    expect(text).toContain('13/10/2026');
    expect(text).toContain('23:12');
  });

  it('puts a moment just after midnight in Algiers on its own day', () => {
    const i18n = TestBed.inject(I18nService);
    const text = i18n.dateTimeIn('fr', '2026-10-13T23:30:00Z');
    expect(text).toContain('14/10/2026');
    expect(text).toContain('00:30');
    expect(i18n.dateIn('fr', '2026-10-13T23:30:00Z')).toContain('14/10/2026');
  });

  it('reads a time that arrives without its zone as UTC, not as the browser clock', () => {
    const i18n = TestBed.inject(I18nService);
    expect(i18n.dateTimeIn('fr', '2026-10-13T23:30:00')).toBe(i18n.dateTimeIn('fr', '2026-10-13T23:30:00Z'));
    expect(i18n.dateTimeIn('fr', '2026-10-13T22:12:00.1234567')).toContain('23:12');
    expect(i18n.dateIn('fr', '2026-10-13T23:30:00')).toContain('14/10/2026');
  });

  it('takes an explicit offset at its word and shows a date alone as that date', () => {
    const i18n = TestBed.inject(I18nService);
    expect(i18n.dateTimeIn('fr', '2026-10-13T23:30:00+01:00')).toContain('23:30');
    expect(i18n.dateIn('fr', '2026-10-13')).toContain('13/10/2026');
  });

  it('formats dates consistently as dd/MM/yyyy in both Arabic and French', () => {
    const i18n = TestBed.inject(I18nService);
    expect(i18n.dateIn('ar', '2026-05-04T12:00:00Z')).toBe('04/05/2026');
    expect(i18n.dateIn('fr', '2026-05-04T12:00:00Z')).toBe('04/05/2026');
    expect(i18n.dateIn('ar', '2026-10-15T12:00:00Z')).toBe('15/10/2026');
    expect(i18n.dateIn('fr', '2026-10-15T12:00:00Z')).toBe('15/10/2026');
    expect(i18n.dateIn('en', '2026-10-15T12:00:00Z')).toBe('15/10/2026');
  });

  it('writes a moment as dd/MM/yyyy HH:mm on a 24-hour clock in every language', () => {
    const i18n = TestBed.inject(I18nService);
    expect(i18n.dateTimeIn('ar', '2026-10-13T22:12:00Z')).toBe('13/10/2026 23:12');
    expect(i18n.dateTimeIn('fr', '2026-10-13T22:12:00Z')).toBe('13/10/2026 23:12');
    expect(i18n.dateTimeIn('en', '2026-05-04T13:05:00Z')).toBe('04/05/2026 14:05');
  });
});
