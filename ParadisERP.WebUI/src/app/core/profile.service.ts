import { Injectable, effect, inject, signal } from '@angular/core';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import { SecureImageService } from './secure-image.service';

/** "My profile" as the server says it (`GET /api/auth/profile`, owner 2026-11-11). */
export interface Profile {
  id: string;
  fullName: string;
  username: string;
  loginId: string | null;
  role: string;
  roleName: string | null;
  branchNameAr: string | null;
  branchNameFr: string | null;
  branchNameEn: string | null;
  employee: { number: string; jobTitle: string | null; phone: string | null; email: string | null; hiredOn: string } | null;
  /** A tag that changes with the photo; null when there is none. */
  photoVersion: string | null;
  twoFactor: TwoFactorStatus;
}

export interface TwoFactorStatus {
  enabled: boolean;
  enabledAtUtc: string | null;
  recoveryCodesLeft: number;
}

export interface TwoFactorSetup {
  /** The key in groups of four, to type by hand when the QR code cannot be scanned. */
  secret: string;
  provisioningUri: string;
  issuer: string;
  account: string;
}

/** The link the photo is read from: the version makes a new photo a new link, never a stale copy. */
export function photoLink(version: string | null | undefined): string | null {
  return version ? `/api/auth/profile/photo?v=${encodeURIComponent(version)}` : null;
}

/**
 * The signed-in user's own account (owner, 2026-11-11): the profile, the photo the top bar shows, and
 * the calls that change two-step login. Online only — none of it is replicated, and the till's offline
 * PIN unlock never involves it.
 */
@Injectable({ providedIn: 'root' })
export class ProfileService {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly images = inject(SecureImageService);

  /** The current user's photo as an `<img>` source, or null: the top bar then shows the initials. */
  readonly photo = signal<string | null>(null);
  private loadedFor: string | null = null;

  constructor() {
    // Another user signed in (or nobody): the photo shown was the previous one's.
    effect(() => {
      const id = this.auth.currentUser()?.id ?? null;
      if (id !== this.loadedFor) {
        this.loadedFor = null;
        this.photo.set(null);
      }
    });
  }

  load(): Promise<Profile> {
    return this.api.getJson<Profile>('/api/auth/profile');
  }

  /** The top bar's photo, read once per signed-in user; a failure leaves the initials, quietly. */
  async ensurePhoto(): Promise<void> {
    const id = this.auth.currentUser()?.id ?? null;
    if (!id || this.loadedFor === id) return;
    this.loadedFor = id;
    try {
      await this.showPhoto((await this.load()).photoVersion);
    } catch {
      this.loadedFor = null;
    }
  }

  async showPhoto(version: string | null): Promise<void> {
    this.photo.set(await this.images.src(photoLink(version)));
  }

  async uploadPhoto(file: Blob): Promise<string> {
    const form = new FormData();
    form.append('file', file, 'photo.jpg');
    const saved = await this.api.postForm<{ photoVersion: string }>('/api/auth/profile/photo', form);
    await this.showPhoto(saved.photoVersion);
    return saved.photoVersion;
  }

  async removePhoto(): Promise<void> {
    await this.api.deleteJson('/api/auth/profile/photo');
    this.photo.set(null);
  }

  setup(password: string): Promise<TwoFactorSetup> {
    return this.api.postJson<TwoFactorSetup>('/api/auth/two-factor/setup', { password });
  }

  enable(code: string): Promise<{ codes: string[] }> {
    return this.api.postJson<{ codes: string[] }>('/api/auth/two-factor/enable', { code });
  }

  disable(password: string): Promise<TwoFactorStatus> {
    return this.api.postJson<TwoFactorStatus>('/api/auth/two-factor/disable', { password });
  }

  newRecoveryCodes(password: string): Promise<{ codes: string[] }> {
    return this.api.postJson<{ codes: string[] }>('/api/auth/two-factor/recovery-codes', { password });
  }
}

/**
 * A picture shrunk to a square of at most `size` pixels, as a JPEG: a phone photo of several
 * megabytes becomes a few dozen kilobytes before it leaves the browser. The centre is kept.
 */
export async function squarePhoto(file: Blob, size = 320): Promise<Blob> {
  const bitmap = await createImageBitmap(file);
  try {
    const side = Math.min(bitmap.width, bitmap.height);
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = Math.min(size, side);
    const context = canvas.getContext('2d');
    if (!context) throw new Error('no canvas');
    context.drawImage(
      bitmap,
      (bitmap.width - side) / 2,
      (bitmap.height - side) / 2,
      side,
      side,
      0,
      0,
      canvas.width,
      canvas.height,
    );
    return await new Promise<Blob>((resolve, reject) =>
      canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('unreadable'))), 'image/jpeg', 0.88),
    );
  } finally {
    bitmap.close();
  }
}
