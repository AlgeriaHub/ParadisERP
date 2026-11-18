import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import { ConnectivityService } from './connectivity.service';
import { db, getMeta } from './db';
import { I18nService } from './i18n.service';
import { Branch } from './models';
import { localizedName } from './multilingual';
import { SecureImageService } from './secure-image.service';
import { SyncService } from './sync.service';

/**
 * The face of the top bar (the owner, 2026-09-30): "on the home page the branch name appears with
 * its logo; if the branch has no logo, take the main branch's logo."
 *
 * Which branch: a cashier's is the terminal's (the till they signed in at); anyone else's is the
 * branch they work at (the login's `branchId`, the main branch for a legacy row); with neither,
 * the main branch.
 *
 * Which logo: that branch's own, else the main branch's, else the company's, else none — and then
 * the bar keeps its text mark. Branches and their logo links come from the synced copy
 * (`db.branches`); the image behind a link is signed-in only, so SecureImageService fetches it and
 * keeps it, and a till that has drawn it once draws it offline. The company logo is only read for
 * the back office, online, from `/api/catalog/store/documents`; a till never waits on it. Printed
 * documents keep the company logo.
 */
@Injectable({ providedIn: 'root' })
export class BranchFace {
  private readonly auth = inject(AuthService);
  private readonly sync = inject(SyncService);
  private readonly api = inject(ApiService);
  private readonly connectivity = inject(ConnectivityService);
  private readonly i18n = inject(I18nService);
  private readonly images = inject(SecureImageService);

  private readonly branches = signal<Branch[]>([]);
  private readonly deviceBranchId = signal<string | null>(null);
  private readonly companyLogo = signal<string | null>(null);
  private companyAsked = false;

  readonly branch = computed(() => {
    const user = this.auth.currentUser();
    const id = faceBranchId(user?.role ?? null, user?.branchId ?? null, this.deviceBranchId(), this.branches());
    return this.branches().find((b) => b.id === id) ?? null;
  });

  readonly name = computed(() => localizedName(this.branch(), this.i18n.lang()));

  /**
   * The branch this terminal sells for — the server books its sales there — else `branch()`. The
   * receipt prints its name, address and identifiers.
   */
  readonly till = computed(() => this.branches().find((b) => b.id === this.deviceBranchId()) ?? this.branch());

  /**
   * Every branch in the device's synced copy, for naming one by id and for the Admin's
   * working-branch menu — drawn from what the till already holds, so neither waits on a request.
   */
  readonly all = this.branches.asReadonly();

  /** A branch's name in the reader's language, or '' when this copy does not know it. */
  nameOf(id: string | null | undefined): string {
    const branch = id ? this.branches().find((b) => b.id === id) : undefined;
    return branch ? localizedName(branch, this.i18n.lang()) : '';
  }
  /** The link the bar should show; `logo` is what an `<img>` can draw from it, once fetched. */
  private readonly logoLink = computed(() => resolveBranchLogo(this.branch(), this.branches(), this.companyLogo()));
  private readonly logoSrc = signal<string | null>(null);
  readonly logo = this.logoSrc.asReadonly();

  constructor() {
    // Re-read the local copy whenever someone signs in and after every pull.
    effect(() => {
      this.auth.currentUser();
      this.sync.stats();
      void this.load();
    });
    // Only the latest link may land: a slow fetch for a branch the user has moved off is dropped.
    effect(() => {
      const link = this.logoLink();
      if (!link) {
        this.logoSrc.set(null);
        return;
      }
      void this.images.src(link).then((src) => {
        if (this.logoLink() === link) this.logoSrc.set(src);
      });
    });
  }

  private async load(): Promise<void> {
    try {
      const [branches, meta] = await Promise.all([db.branches.toArray(), getMeta()]);
      this.branches.set(branches.filter((b) => !b.isDeleted));
      this.deviceBranchId.set(meta.deviceBranchId ?? null);
    } catch {
      /* the text mark stays; nothing here may stand in the way of the till */
    }
    const role = this.auth.currentUser()?.role;
    if (!this.companyAsked && (role === 'Manager' || role === 'Admin') && this.connectivity.online()) {
      this.companyAsked = true;
      try {
        const docs = await this.api.getJson<{ logoUrl?: string | null }>('/api/catalog/store/documents');
        this.companyLogo.set(docs.logoUrl ?? null);
      } catch {
        this.companyAsked = false;
      }
    }
  }
}

/** See {@link BranchFace}: the terminal's branch for a cashier, the user's otherwise, else the main one. */
export function faceBranchId(
  role: string | null,
  userBranchId: string | null,
  deviceBranchId: string | null,
  branches: readonly { id: string; isMain: boolean; isWarehouse?: boolean }[],
): string | null {
  const known = (id: string | null) => (id !== null && branches.some((b) => b.id === id) ? id : null);
  const first = role === 'Cashier' ? known(deviceBranchId) ?? known(userBranchId) : known(userBranchId) ?? known(deviceBranchId);
  // The main branch — the head office — not the main warehouse (owner, 2026-10-05: two kinds).
  return first ?? branches.find((b) => b.isMain && !b.isWarehouse)?.id ?? null;
}

/** The branch's logo link, else the main branch's, else the company's, else null (the text mark). */
export function resolveBranchLogo(
  branch: { logoUrl?: string | null } | null,
  branches: readonly { isMain: boolean; isWarehouse?: boolean; logoUrl?: string | null }[],
  companyLogo: string | null,
): string | null {
  return branch?.logoUrl || branches.find((b) => b.isMain && !b.isWarehouse)?.logoUrl || companyLogo || null;
}
