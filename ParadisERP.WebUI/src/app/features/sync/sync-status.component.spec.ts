import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConnectivityService } from '../../core/connectivity.service';
import { patchMeta } from '../../core/db';
import { I18nService } from '../../core/i18n.service';
import { PosService } from '../../core/pos.service';
import { SyncService, SyncStats } from '../../core/sync.service';
import { ConfirmService } from '../../ui/confirm.service';
import { SyncStatusComponent } from './sync-status.component';

/**
 * What the live offline test of 2026-10-05 found on the till's sync page, read in Arabic as the
 * owner reads it.
 */
describe('sync page — what it says', () => {
  const doc = document as unknown as { startViewTransition?: unknown };
  let original: unknown;
  let stored: string | null;
  let html: { lang: string; dir: string };
  const stats = signal<SyncStats>({
    pending: 0, rejected: 0, synced: 0, products: 1, customers: 0, cursor: 10, lastSyncAtUtc: null, lastError: null,
  });

  beforeEach(async () => {
    // The language choice is remembered on the device and on <html>; put both back afterwards.
    stored = localStorage.getItem('pos.lang');
    html = { lang: document.documentElement.lang, dir: document.documentElement.dir };
    original = doc.startViewTransition;
    doc.startViewTransition = (update: () => void) => {
      update();
      return {};
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: SyncService, useValue: { stats, syncing: signal(false), refreshStats: () => Promise.resolve(stats()), sync: () => Promise.resolve(), fullResync: () => Promise.resolve() } },
        { provide: ConnectivityService, useValue: { online: signal(false) } },
        { provide: PosService, useValue: { recentSales: () => Promise.resolve([]), loadCatalog: () => Promise.resolve() } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    // The live site serves the app and the API from one address, so no server is stored.
    await patchMeta({ apiBaseUrl: '' });
    TestBed.inject(I18nService).setLang('ar');
  });

  afterEach(() => {
    doc.startViewTransition = original;
    if (stored === null) localStorage.removeItem('pos.lang');
    else localStorage.setItem('pos.lang', stored);
    document.documentElement.lang = html.lang;
    document.documentElement.dir = html.dir;
  });

  async function render() {
    const fixture = TestBed.createComponent(SyncStatusComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const rowText = (page: HTMLElement, label: string) =>
    [...page.querySelectorAll('.details .row')].find((r) => r.textContent!.includes(label))?.textContent ?? '';

  it('never says everything is synced while a sale is waiting', async () => {
    stats.update((s) => ({ ...s, pending: 1, synced: 0 }));
    const page = await render();
    const tiles = [...page.querySelectorAll('.tiles .tile')].map(
      (t) => `${t.querySelector('span')!.textContent!.trim()} = ${t.querySelector('strong')!.textContent!.trim()}`,
    );
    expect(tiles).toContain('مبيعات في الانتظار = 1');
    // The third tile counts sales already sent; it is named as a count, not as a state.
    expect(tiles).toContain('مبيعات متزامنة = 0');
    expect(page.textContent).not.toContain('كل شيء متزامن');
  });

  it('names a same-address server in the reader’s language', async () => {
    const page = await render();
    expect(rowText(page, 'الخادم')).toContain('نفس عنوان التطبيق');
    expect(page.textContent).not.toContain('même origine');
  });
});
