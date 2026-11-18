import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';

/** The Cache Storage the logos are kept in, apart from the service worker's own caches. */
export const SECURE_IMAGE_CACHE = 'pos-secure-images-v1';

/**
 * Images the server serves to signed-in users only (the owner, 2026-10-04: files live in a folder
 * nothing outside the application can reach, and the database keeps only their path). An `<img>`
 * cannot send the bearer token, so a link like `/api/catalog/branches/{id}/logo?v=…` is fetched
 * here with it and handed to the template as an object URL.
 *
 * <p>The links are versioned — a new logo is a new `?v=` — so a copy once fetched never goes
 * stale. Each one is kept in Cache Storage and read from there first: a till that has drawn its
 * branch's logo once draws it offline, and never asks again. Where Cache Storage is unavailable
 * (plain http on a LAN address) the copy lives in memory for the session.</p>
 *
 * <p>Nothing here throws into a screen: an image that cannot be had is null, and the screen keeps
 * its text mark.</p>
 */
@Injectable({ providedIn: 'root' })
export class SecureImageService {
  private readonly api = inject(ApiService);
  private readonly objectUrls = new Map<string, Promise<string | null>>();

  /**
   * A `src` an `<img>` can show for a stored logo value: a `data:` image as it is (a file just
   * picked, not yet saved), a signed-in `/api/` link as an object URL, anything else null.
   */
  src(value: string | null | undefined): Promise<string | null> {
    if (!value) return Promise.resolve(null);
    if (value.startsWith('data:image/')) return Promise.resolve(value);
    if (!isApiLink(value)) return Promise.resolve(null);

    let pending = this.objectUrls.get(value);
    if (!pending) {
      pending = this.blob(value).then((blob) => (blob ? displayUrl(blob) : null));
      this.objectUrls.set(value, pending);
      // A miss is not remembered: the next screen to ask tries again (signed in again, back online).
      void pending.then((url) => {
        if (url === null) this.objectUrls.delete(value);
      });
    }
    return pending;
  }

  /**
   * The same image as a `data:` URL, for paper: a print dialog opened before a second request
   * came back would print an empty frame, so a document carries its logo inline.
   */
  async dataUrl(value: string | null | undefined): Promise<string | null> {
    if (!value) return null;
    if (value.startsWith('data:image/')) return value;
    if (!isApiLink(value)) return null;
    const blob = await this.blob(value);
    if (!blob) return null;
    try {
      return await readAsDataUrl(blob);
    } catch {
      return null;
    }
  }

  private async blob(link: string): Promise<Blob | null> {
    const kept = await this.recall(link);
    if (kept) return kept;
    try {
      const blob = await this.api.getBlob(link);
      void this.keep(link, blob);
      return blob;
    } catch {
      return null;
    }
  }

  private async recall(link: string): Promise<Blob | null> {
    try {
      const cache = await caches.open(SECURE_IMAGE_CACHE);
      const hit = await cache.match(link);
      return hit ? await hit.blob() : null;
    } catch {
      return null;
    }
  }

  /** Keeps this version and drops any older one of the same image, so the cache holds one each. */
  private async keep(link: string, blob: Blob): Promise<void> {
    try {
      const cache = await caches.open(SECURE_IMAGE_CACHE);
      const path = new URL(link, location.origin).pathname;
      for (const request of await cache.keys()) {
        const url = new URL(request.url);
        if (url.pathname === path && `${url.pathname}${url.search}` !== link) await cache.delete(request);
      }
      await cache.put(link, new Response(blob, { headers: { 'Content-Type': blob.type || 'application/octet-stream' } }));
    } catch {
      /* memory only: the object URL above still serves this session */
    }
  }
}

/**
 * What an `<img>` shows a fetched image from. An object URL belongs to this site's origin, so an
 * SVG opened from it in a tab of its own would run as part of the app; an SVG is shown from a
 * `data:` URL instead, which a browser never opens as a page (security review, 2026-10-10).
 */
async function displayUrl(blob: Blob): Promise<string | null> {
  if (blob.type !== 'image/svg+xml') return URL.createObjectURL(blob);
  try {
    return await readAsDataUrl(blob);
  } catch {
    return null;
  }
}

/** A link this service may fetch: the API's own path, never another origin. */
function isApiLink(value: string): boolean {
  return value.startsWith('/api/');
}

export function readAsDataUrl(file: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => (typeof reader.result === 'string' ? resolve(reader.result) : reject(new Error('unreadable')));
    reader.onerror = () => reject(reader.error ?? new Error('unreadable'));
    reader.readAsDataURL(file);
  });
}
