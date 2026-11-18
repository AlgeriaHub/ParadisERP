import { Injectable, effect, inject, signal, untracked } from '@angular/core';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import { NavUser, holds, keyed } from './landing';

/** The keys whose holders may read Maintenance › Settings (`RoutePermissions`: `GET /api/maintenance/settings`). */
const SETTINGS_READERS: readonly string[] = ['maintenance.view', 'maintenance.settings', 'accounting.manage'];

/** Whether this user may ask the server whether the module is on: a reader of the settings, or before keys a manager or the Admin. */
export function mayReadMaintenanceSettings(user: NavUser | null | undefined): boolean {
  if (!user) return false;
  return keyed(user) ? SETTINGS_READERS.some((k) => holds(user, k)) : user.role === 'Manager' || user.role === 'Admin';
}

/**
 * MS-01, the maintenance module, as the server says it (Phase M1, docs/maintenance/plan.md §7): the module's job pages
 * show in the sidebar only while it is on. Read once per signed-in user from `GET /api/maintenance/settings` — by whoever
 * may read it — and set again when the settings page saves. Unknown (null) until it answers, and off when it cannot be
 * read: a page the server would answer 409 `maintenance_off` is not offered.
 */
@Injectable({ providedIn: 'root' })
export class MaintenanceModule {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);

  /** True on, false off, null not known yet. */
  readonly on = signal<boolean | null>(null);
  private pending: Promise<boolean> | null = null;
  private readFor: string | null = null;

  constructor() {
    // A new sign-in asks again; a sign-out forgets.
    effect(() => {
      const user = this.auth.currentUser();
      untracked(() => {
        const id = user ? `${(user as { id?: string }).id ?? ''}` : null;
        if (id === this.readFor) return;
        this.readFor = id;
        this.pending = null;
        this.on.set(null);
        if (user) void this.load();
      });
    });
  }

  /** Whether the module is on, asking the server the first time. */
  load(): Promise<boolean> {
    if (this.pending) return this.pending;
    const user = this.auth.currentUser();
    if (!mayReadMaintenanceSettings(user)) {
      this.on.set(false);
      return (this.pending = Promise.resolve(false));
    }
    this.pending = this.api
      .getJson<{ module: string }>('/api/maintenance/settings')
      .then((s) => s.module === 'On')
      .catch(() => false)
      .then((on) => {
        this.on.set(on);
        return on;
      });
    return this.pending;
  }

  /** The settings page saved: the sidebar follows at once. */
  set(on: boolean): void {
    this.pending = Promise.resolve(on);
    this.on.set(on);
  }
}
