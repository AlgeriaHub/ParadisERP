import { Injectable, computed, inject, signal } from '@angular/core';
import { AuthService } from './auth.service';
import { BranchLock } from './branch-lock';

/**
 * The Admin's "working branch" (REDESIGN_PLAN.md §8, decision D2): one branch, or every branch.
 *
 * **Screen context only.** It pre-fills the branch filters and branch fields that already exist;
 * it is never sent to the server as anything but the value of such a field, never read by a guard
 * or an access rule, and it replaces nothing the server enforces (`AtCallersBranch`,
 * `BranchLock`). A locked user has no working branch: they get their own, read-only, and no
 * control is drawn for them.
 *
 * Kept per user in `localStorage`, a per-viewer convenience: when storage is unavailable the
 * Admin simply starts on their own branch, as every form does today (`BranchLock.defaultBranch`).
 */
export type WorkingBranch = { kind: 'branch'; id: string } | { kind: 'all' };

/** What a screen's endpoint can show, read from its signature (REDESIGN_PLAN.md §8.2). */
export type BranchSupport =
  /** `branchId` optional, null = no filter: one branch or every branch. */
  | 'branchOrAll'
  /** The endpoint needs one branch (the attendance sheet, a stock document). */
  | 'oneBranch'
  /** The ledger: null means the group, consolidated — never "all branches". */
  | 'branchOrGroup'
  /** No branch parameter at all: the data is the company's. */
  | 'company';

export type ScopeLabel = 'branch' | 'all' | 'group' | 'company';

export interface ResolvedScope {
  /** What the screen's branch filter should start on; null means "no filter" (all / group / none). */
  branchId: string | null;
  label: ScopeLabel;
}

/**
 * Where a screen starts, given the working branch and what its endpoint supports. Pure, so the
 * whole table is pinned by `branch-scope.service.spec.ts`.
 *
 * - A locked user is always on their own branch, except on company-wide data.
 * - "All branches" is honoured only where the endpoint takes it; elsewhere the screen opens on
 *   the Admin's own branch and says which branch it is showing.
 */
export function resolveScope(
  working: WorkingBranch,
  lockedId: string | null,
  ownId: string | null,
  support: BranchSupport,
): ResolvedScope {
  if (support === 'company') return { branchId: null, label: 'company' };
  if (lockedId !== null) return { branchId: lockedId, label: 'branch' };
  if (working.kind === 'branch') return { branchId: working.id, label: 'branch' };
  if (support === 'branchOrAll') return { branchId: null, label: 'all' };
  if (support === 'branchOrGroup') return { branchId: null, label: 'group' };
  return { branchId: ownId, label: 'branch' };
}

const STORAGE_PREFIX = 'pos.workingBranch.';

@Injectable({ providedIn: 'root' })
export class BranchScopeService {
  private readonly auth = inject(AuthService);
  private readonly lock = inject(BranchLock);

  /** Bumped on every change so `working` re-reads storage; storage itself is not a signal. */
  private readonly version = signal(0);

  /** Whether this user may choose a working branch at all: the Admin, never a locked user. */
  readonly canChoose = computed(() => !!this.auth.currentUser() && !this.lock.locked());

  readonly working = computed<WorkingBranch>(() => {
    this.version();
    const user = this.auth.currentUser();
    const own = this.lock.ownBranchId();
    const fallback: WorkingBranch = own ? { kind: 'branch', id: own } : { kind: 'all' };
    if (!user || this.lock.locked()) return fallback;
    return readStored(user.id) ?? fallback;
  });

  set(next: WorkingBranch): void {
    const user = this.auth.currentUser();
    if (!user || !this.canChoose()) return;
    try {
      localStorage.setItem(STORAGE_PREFIX + user.id, JSON.stringify(next));
    } catch {
      // Storage refused (private window, blocked site data): the choice lasts this session only.
      sessionChoice.set(user.id, next);
    }
    this.version.update((n) => n + 1);
  }

  resolve(support: BranchSupport): ResolvedScope {
    return resolveScope(this.working(), this.lock.lockedBranchId(), this.lock.ownBranchId(), support);
  }
}

/**
 * Where a document form's existing branch field starts (§8.1): the working branch when it names
 * one the form offers; with *All branches*, or a branch the form does not list, today's default
 * (`BranchLock.defaultBranch`) — so a document is never raised at a branch the reader did not see
 * on the form. A locked user always gets their own.
 */
export function startBranch<T extends { id: string; isMain?: boolean }>(
  scope: { working(): WorkingBranch },
  lock: { lockedBranchId(): string | null; defaultBranch(branches: T[]): string | null },
  branches: T[],
): string | null {
  if (lock.lockedBranchId() !== null) return lock.lockedBranchId();
  const working = scope.working();
  if (working.kind === 'branch' && branches.some((b) => b.id === working.id)) return working.id;
  return lock.defaultBranch(branches);
}

/** The fallback when `localStorage` throws: the choice survives navigation, not a reload. */
const sessionChoice = new Map<string, WorkingBranch>();

function readStored(userId: string): WorkingBranch | null {
  try {
    const raw = localStorage.getItem(STORAGE_PREFIX + userId);
    if (raw) return parseWorking(raw);
  } catch {
    // fall through to the in-memory choice
  }
  return sessionChoice.get(userId) ?? null;
}

/** Anything that is not a well-formed choice reads as no choice, never as "all branches". */
export function parseWorking(raw: string): WorkingBranch | null {
  try {
    const value = JSON.parse(raw) as Partial<WorkingBranch> & { id?: unknown };
    if (value?.kind === 'all') return { kind: 'all' };
    if (value?.kind === 'branch' && typeof value.id === 'string' && value.id) return { kind: 'branch', id: value.id };
  } catch {
    // not JSON
  }
  return null;
}
