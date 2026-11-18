import { Injectable, computed, inject, signal } from '@angular/core';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { ResolvedScope, WorkingBranch, parseWorking } from '../../../core/branch-scope.service';

/**
 * The dashboard's own branch choice (owner's decision, 2026-10-03) — **not** the working branch
 * (D2). The dashboard neither reads nor writes `BranchScopeService`, so choosing "all branches"
 * here never carries into another page, and another page's choice never lands here.
 *
 * - A locked user (a branch Manager) is on their own branch, and no control is drawn.
 * - The Admin, before choosing anything here: every branch when the company has more than one
 *   active branch, else that one branch.
 * - Once the Admin chooses here, the choice is kept per user across reloads, like the working
 *   branch: `localStorage`, with an in-memory fallback when storage refuses.
 */
export interface DashboardScopeInput {
  lockedId: string | null;
  stored: WorkingBranch | null;
  /** The company's branches, or null while the list is not known (still loading, or it failed). */
  branches: readonly { id: string; isActive?: boolean }[] | null;
}

/** Where the dashboard looks. Pure, so the whole table is pinned by `dashboard-scope.spec.ts`. */
export function dashboardScope({ lockedId, stored, branches }: DashboardScopeInput): ResolvedScope {
  if (lockedId !== null) return { branchId: lockedId, label: 'branch' };
  if (stored?.kind === 'all') return { branchId: null, label: 'all' };
  // A remembered branch that has since been removed from the company reads as no choice.
  if (stored?.kind === 'branch' && (branches === null || branches.some((b) => b.id === stored.id))) {
    return { branchId: stored.id, label: 'branch' };
  }
  const active = (branches ?? []).filter((b) => b.isActive !== false);
  if (active.length === 1) return { branchId: active[0].id, label: 'branch' };
  return { branchId: null, label: 'all' };
}

const STORAGE_PREFIX = 'pos.dashboardBranch.';

/** The fallback when `localStorage` throws: the choice survives navigation, not a reload. */
const sessionChoice = new Map<string, WorkingBranch>();

@Injectable({ providedIn: 'root' })
export class DashboardScope {
  private readonly auth = inject(AuthService);
  private readonly lock = inject(BranchLock);

  /** Bumped on every change so `stored` re-reads storage; storage itself is not a signal. */
  private readonly version = signal(0);

  /** Only the Admin chooses; a locked user's dashboard is their branch's. */
  readonly canChoose = computed(() => !!this.auth.currentUser() && !this.lock.locked());

  /** The Admin's remembered dashboard choice, or null when they have not chosen here yet. */
  readonly stored = computed<WorkingBranch | null>(() => {
    this.version();
    const user = this.auth.currentUser();
    if (!user || !this.canChoose()) return null;
    try {
      const raw = localStorage.getItem(STORAGE_PREFIX + user.id);
      if (raw) return parseWorking(raw);
    } catch {
      // fall through to the in-memory choice
    }
    return sessionChoice.get(user.id) ?? null;
  });

  readonly lockedId = computed(() => this.lock.lockedBranchId());

  set(next: WorkingBranch): void {
    const user = this.auth.currentUser();
    if (!user || !this.canChoose()) return;
    try {
      localStorage.setItem(STORAGE_PREFIX + user.id, JSON.stringify(next));
    } catch {
      sessionChoice.set(user.id, next);
    }
    this.version.update((n) => n + 1);
  }
}
