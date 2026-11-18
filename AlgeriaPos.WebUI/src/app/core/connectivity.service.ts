import { Injectable, NgZone, signal } from '@angular/core';

/**
 * navigator.onLine only tells us there is *a* network — a shop on a dead ADSL line still
 * reports true. So the real signal is a cheap probe against the API's own /health endpoint,
 * re-run on the browser events and on a slow timer.
 */
@Injectable({ providedIn: 'root' })
export class ConnectivityService {
  /** True only when the API answered a probe recently. */
  readonly online = signal<boolean>(navigator.onLine);
  readonly lastProbeAtUtc = signal<string | null>(null);

  private baseUrl = '';
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor(private zone: NgZone) {
    window.addEventListener('online', () => void this.probe());
    window.addEventListener('offline', () => this.online.set(false));
  }

  configure(baseUrl: string): void {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    if (this.timer) clearInterval(this.timer);
    // Outside Angular's zone so the poll doesn't trigger change detection every 20s.
    this.zone.runOutsideAngular(() => {
      this.timer = setInterval(() => void this.probe(), 20_000);
    });
    void this.probe();
  }

  async probe(): Promise<boolean> {
    if (!navigator.onLine) {
      this.zone.run(() => this.online.set(false));
      return false;
    }
    try {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 4000);
      const response = await fetch(`${this.baseUrl}/api/auth/ping`, {
        method: 'GET',
        cache: 'no-store',
        signal: controller.signal,
      });
      clearTimeout(timeout);
      const ok = response.ok;
      this.zone.run(() => {
        this.online.set(ok);
        this.lastProbeAtUtc.set(new Date().toISOString());
      });
      return ok;
    } catch {
      this.zone.run(() => this.online.set(false));
      return false;
    }
  }
}
