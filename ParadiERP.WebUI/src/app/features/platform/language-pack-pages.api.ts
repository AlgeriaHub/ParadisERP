import { Injectable } from '@angular/core';
import { PageResult, pageQueryString } from '../../core/paging';
import { LanguagePackSummary, PlatformError } from '../../core/platform.service';

export interface LanguagePacksPageQuery {
  skip: number;
  take: number;
  direction?: 'all' | 'ltr' | 'rtl';
}

/**
 * The language packs a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend"):
 * `GET /api/platform/language-packs/page` beside the list route, on the operator's own policy
 * (ParadiERP.API/Endpoints/Paging/PeoplePageRoutes.cs). A read, so it carries no antiforgery token; otherwise it is
 * asked exactly as `PlatformService` asks — same origin, never cached, the same time limit and the same error.
 */
@Injectable({ providedIn: 'root' })
export class LanguagePackPagesApi {
  async languagePacksPage(query: LanguagePacksPageQuery): Promise<PageResult<LanguagePackSummary>> {
    const response = await fetch(`/api/platform/language-packs/page${pageQueryString({ ...query })}`, {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
      signal: AbortSignal.timeout(30000),
    });
    if (!response.ok) {
      const text = await response.text();
      let message = text;
      try {
        const data = JSON.parse(text);
        message = typeof data === 'string' ? data : (data.detail ?? text);
      } catch {
        /* not JSON — the body is already a sentence */
      }
      throw new PlatformError(message || `HTTP ${response.status}`, response.status);
    }
    return response.json() as Promise<PageResult<LanguagePackSummary>>;
  }
}
