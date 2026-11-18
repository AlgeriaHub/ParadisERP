import { Injectable, computed, inject } from '@angular/core';
import { AuthService } from './auth.service';
import { I18nService } from './i18n.service';
import { MultilingualName, localizedName } from './multilingual';

/**
 * The owner's rule for branches, 2026-09-30 — final: "in the whole program, the branch choice does
 * not appear except for the super admin".
 *
 * - **The Admin is free.** Every branch select exists as before; filters offer "all branches"; a
 *   document form starts on his assigned branch (`ownBranchId`) and he may change it.
 * - **Everyone else is locked** to the branch assigned to them — the main branch grants nothing.
 *   No branch select anywhere: document forms show the branch as plain text, list and report
 *   filters are hidden and read their branch only.
 *
 * The server decides both halves (`UserDto.BranchLocked`, `BranchId`) and enforces them: for a
 * non-admin it replaces the branch of every document raised and every list filter with theirs
 * (API `CallersBranch`). This is the screen half, so the choice is not offered in the first place.
 *
 * A session stored before this existed carries no branch and reads as not locked until the next
 * online sign-in; the server still holds the line meanwhile.
 */
@Injectable({ providedIn: 'root' })
export class BranchLock {
  private readonly auth = inject(AuthService);
  private readonly i18n = inject(I18nService);

  /** The branch the signed-in user works at, whatever their role; null before any online sign-in. */
  readonly ownBranchId = computed(() => this.auth.currentUser()?.branchId ?? null);

  /** True when this user may not choose a branch. Needs a known branch: without one, nothing is locked. */
  readonly locked = computed(() => this.auth.currentUser()?.branchLocked === true && this.ownBranchId() !== null);

  /**
   * Whether this user may look up a product's available quantity at the other branches: the
   * Admin always; anyone else only with the `stock.view_other_branches` grant (the owner: "if he
   * has been given the permission"). A grant or a revocation revokes the session, so the next
   * sign-in carries the new answer.
   */
  readonly mayLookUpOtherBranches = computed(() =>
    mayLookUpOtherBranches(this.locked(), this.auth.currentUser()?.permissions ?? []),
  );

  /** The branch a locked user is confined to, else null (the Admin, or a stale session). */
  readonly lockedBranchId = computed(() => (this.locked() ? this.ownBranchId() : null));

  /** The branches a select may offer this user: all of them, or only their own. */
  scope<T extends { id: string }>(branches: T[]): T[] {
    return scopeBranches(branches, this.lockedBranchId());
  }

  /**
   * Rows that belong to a branch — cash boxes to pay from, above all: every row, or only the locked
   * branch's. The server refuses another branch's box for a non-admin; this keeps it off the list.
   */
  scopeRows<T extends { branchId: string }>(rows: T[]): T[] {
    return scopeRowsTo(rows, this.lockedBranchId());
  }

  /**
   * Where a document form starts: the locked branch; else the admin's own branch when the list has
   * it; else the main branch, the list's first entry, or `fallback`.
   */
  defaultBranch<T extends { id: string; isMain?: boolean; isWarehouse?: boolean }>(branches: T[], fallback: string | null = null): string | null {
    return defaultBranchId(branches, this.lockedBranchId(), this.ownBranchId(), fallback);
  }

  /** The user's own branch by name, in the reader's language, for the plain-text line a locked form shows. */
  ownBranchName(branches: (Partial<MultilingualName> & { id: string })[]): string {
    const own = this.ownBranchId();
    return localizedName(branches.find((b) => b.id === own), this.i18n.lang()) || '—';
  }
}

/** The grant that opens the other branches' availability (Domain/Permissions.ViewOtherBranchStock). */
export const VIEW_OTHER_BRANCH_STOCK = 'stock.view_other_branches';

/** See {@link BranchLock.mayLookUpOtherBranches}. */
export function mayLookUpOtherBranches(locked: boolean, permissions: readonly string[]): boolean {
  return !locked || permissions.includes(VIEW_OTHER_BRANCH_STOCK);
}

/** Only the locked branch when there is one, else the list untouched. Pure, so it is pinned by a spec. */
export function scopeBranches<T extends { id: string }>(branches: T[], lockedId: string | null): T[] {
  return lockedId === null ? branches : branches.filter((b) => b.id === lockedId);
}

/** Only the rows of the locked branch when there is one, else every row. */
export function scopeRowsTo<T extends { branchId: string }>(rows: T[], lockedId: string | null): T[] {
  return lockedId === null ? rows : rows.filter((r) => r.branchId === lockedId);
}

/** See {@link BranchLock.defaultBranch}. A locked user always gets their own branch, listed or not. */
export function defaultBranchId<T extends { id: string; isMain?: boolean; isWarehouse?: boolean }>(
  branches: T[],
  lockedId: string | null,
  ownId: string | null,
  fallback: string | null = null,
): string | null {
  if (lockedId !== null) return lockedId;
  if (ownId !== null && branches.some((b) => b.id === ownId)) return ownId;
  return branches.find((b) => b.isMain && !b.isWarehouse)?.id ?? branches[0]?.id ?? fallback;
}
