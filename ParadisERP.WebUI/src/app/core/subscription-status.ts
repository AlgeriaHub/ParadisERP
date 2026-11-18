import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { liveQuery } from 'dexie';
import { db, getMeta } from './db';
import { NavUser } from './landing';
import { PosUser } from './models';

/**
 * The company's subscription status as the server last reported it — written to the device's meta
 * by the sign-in and by every pull — for the navigation rules (`core/nav.ts`). Watched with a live
 * query, so the sidebar follows a pull without the sign-in or the sync code knowing about it.
 */
@Injectable({ providedIn: 'root' })
export class SubscriptionStatus {
  readonly status = signal<string | null>(null);

  constructor() {
    const watch = liveQuery(() => db.meta.get('device')).subscribe({
      next: (meta) => this.status.set(meta?.subscription?.status ?? null),
      error: () => this.status.set(null),
    });
    inject(DestroyRef).onDestroy(() => watch.unsubscribe());
  }
}

/**
 * The user as the navigation rules read them: who they are, what the company's status is and, when known, whether the
 * maintenance module is on (MS-01).
 */
export function navUser(
  user: PosUser | null | undefined,
  status: string | null | undefined,
  maintenanceOn: boolean | null = null,
): NavUser | null {
  return user ? { ...user, subscriptionStatus: status ?? null, maintenanceOn } : null;
}

/** The status read straight from the device, for a guard that runs before the live query answers. */
export async function storedSubscriptionStatus(): Promise<string | null> {
  try {
    return (await getMeta()).subscription?.status ?? null;
  } catch {
    return null;
  }
}
