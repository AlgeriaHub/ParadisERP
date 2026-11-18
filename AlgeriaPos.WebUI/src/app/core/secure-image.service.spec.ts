import { TestBed } from '@angular/core/testing';
import { ApiError, ApiService } from './api.service';
import { SECURE_IMAGE_CACHE, SecureImageService } from './secure-image.service';

/**
 * Logos are files only a signed-in user may read: fetched with the token, drawn from an object
 * URL, kept for a till that goes offline, and never an error a screen has to catch.
 */
describe('SecureImageService', () => {
  const png = () => new Blob(['png'], { type: 'image/png' });
  let getBlob: jasmine.Spy;
  let images: SecureImageService;

  /** keep() is not awaited by the screen; the spec waits for it to land. */
  async function kept(link: string): Promise<Response | undefined> {
    const cache = await caches.open(SECURE_IMAGE_CACHE);
    for (let i = 0; i < 50; i++) {
      const hit = await cache.match(link);
      if (hit) return hit;
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    return undefined;
  }

  beforeEach(async () => {
    await caches.delete(SECURE_IMAGE_CACHE);
    getBlob = jasmine.createSpy('getBlob');
    TestBed.configureTestingModule({ providers: [{ provide: ApiService, useValue: { getBlob } }] });
    images = TestBed.inject(SecureImageService);
  });

  afterEach(async () => {
    await caches.delete(SECURE_IMAGE_CACHE);
  });

  it('passes a picked data image through and fetches nothing that is not the API', async () => {
    expect(await images.src('data:image/png;base64,AAAA')).toBe('data:image/png;base64,AAAA');
    expect(await images.src('https://example.com/logo.png')).toBeNull();
    expect(await images.src(null)).toBeNull();
    expect(await images.dataUrl('https://example.com/logo.png')).toBeNull();
    expect(getBlob).not.toHaveBeenCalled();
  });

  it('fetches a signed-in link once and draws it as an object URL', async () => {
    getBlob.and.resolveTo(png());
    const link = '/api/catalog/store/logo?v=aaaaaaaaaaaa';
    const first = await images.src(link);
    expect(first).toMatch(/^blob:/);
    expect(await images.src(link)).toBe(first);
    expect(getBlob).toHaveBeenCalledOnceWith(link);
  });

  it('draws a kept logo offline, and inlines it for paper', async () => {
    const link = '/api/catalog/branches/b1/logo?v=bbbbbbbbbbbb';
    getBlob.and.resolveTo(png());
    await images.src(link);
    expect(await kept(link)).toBeDefined();

    // A new session on a till that has lost the line.
    getBlob.and.rejectWith(new ApiError('Network error', 0));
    const offline = TestBed.runInInjectionContext(() => new SecureImageService());
    expect(await offline.src(link)).toMatch(/^blob:/);
    expect(await offline.dataUrl(link)).toBe('data:image/png;base64,' + btoa('png'));
  });

  it('keeps one version of an image: a new logo drops the old copy', async () => {
    getBlob.and.resolveTo(png());
    await images.src('/api/catalog/branches/b1/logo?v=111111111111');
    expect(await kept('/api/catalog/branches/b1/logo?v=111111111111')).toBeDefined();
    await images.src('/api/catalog/branches/b1/logo?v=222222222222');
    expect(await kept('/api/catalog/branches/b1/logo?v=222222222222')).toBeDefined();

    const cache = await caches.open(SECURE_IMAGE_CACHE);
    expect(await cache.match('/api/catalog/branches/b1/logo?v=111111111111')).toBeUndefined();
  });

  it('is null, never an error, when the logo cannot be had — and asks again next time', async () => {
    const link = '/api/catalog/store/logo?v=cccccccccccc';
    getBlob.and.rejectWith(new ApiError('Not found', 404));
    expect(await images.src(link)).toBeNull();
    expect(await images.dataUrl(link)).toBeNull();

    getBlob.and.resolveTo(png());
    expect(await images.src(link)).toMatch(/^blob:/);
  });
});
