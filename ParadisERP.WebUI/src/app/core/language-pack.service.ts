import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';
import { db } from './db';
import { I18nService } from './i18n.service';
import { LanguagePack } from './models';

/** Keeps operator-uploaded translations in IndexedDB and hands them to the i18n service. */
@Injectable({ providedIn: 'root' })
export class LanguagePackService {
  private readonly api = inject(ApiService);
  private readonly i18n = inject(I18nService);

  async loadCached(): Promise<void> {
    this.i18n.setPacks(await db.languagePacks.toArray());
  }

  /**
   * Downloads packs whose server revision differs from the cached copy and drops packs the
   * company no longer enables. A failed download keeps the old copy; the next sync retries.
   */
  async sync(revisions: Record<string, number> | undefined): Promise<void> {
    if (!revisions) return;
    const cached = await db.languagePacks.toArray();
    const stale = cached.filter((p) => !(p.code in revisions)).map((p) => p.code);
    if (stale.length) await db.languagePacks.bulkDelete(stale);
    for (const [code, revision] of Object.entries(revisions)) {
      if (cached.find((p) => p.code === code)?.revision === revision) continue;
      try {
        const pack = await this.api.getJson<LanguagePack>(`/api/i18n/${encodeURIComponent(code)}`);
        await db.languagePacks.put({ ...pack, revision });
      } catch {
        /* offline or removed meanwhile */
      }
    }
    await this.loadCached();
  }
}
