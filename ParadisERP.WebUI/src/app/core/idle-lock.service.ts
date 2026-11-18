import { Injectable, NgZone, inject } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Thirty minutes without a key or a click in the back office signs the session out. */
export const IDLE_LOCK_MS = 30 * 60_000;
const CHECK_EVERY_MS = 30_000;

/** The back-office pages the idle lock watches; the till's own screens are never locked. */
export function watchedByIdleLock(url: string): boolean {
  return /^\/(backoffice|office|change-password|account)(\/|\?|$)/.test(url);
}

/**
 * The back office locks itself when left alone (owner, security review 2026-10-10): a manager who
 * walked away from a shared computer left their back office open to the next person for as long as
 * the session lasted. After {@link IDLE_LOCK_MS} without input on a back-office page the session is
 * signed out — on the server too (AuthService.logout) — and the sign-in screen says why. The register
 * is never locked: rule 1, nothing stands between a cashier and a sale.
 */
@Injectable({ providedIn: 'root' })
export class IdleLockService {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly zone = inject(NgZone);
  private lastActive = Date.now();
  private started = false;

  start(): void {
    if (this.started) return;
    this.started = true;
    const touch = () => (this.lastActive = Date.now());
    this.zone.runOutsideAngular(() => {
      for (const event of ['pointerdown', 'keydown', 'wheel', 'touchstart']) {
        window.addEventListener(event, touch, { passive: true, capture: true });
      }
      setInterval(() => void this.check(), CHECK_EVERY_MS);
    });
  }

  /** Public for the spec: signs out when the session has been idle long enough on a watched page. */
  async check(now = Date.now()): Promise<boolean> {
    if (!this.auth.currentUser() || !watchedByIdleLock(this.router.url)) return false;
    if (now - this.lastActive < IDLE_LOCK_MS) return false;
    this.lastActive = now;
    await this.zone.run(async () => {
      await this.auth.logout();
      await this.router.navigate(['/login'], { queryParams: { reason: 'idle' } });
    });
    return true;
  }
}
