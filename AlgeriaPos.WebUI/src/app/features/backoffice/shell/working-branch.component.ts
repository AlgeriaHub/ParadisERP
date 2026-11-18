import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { BranchFace } from '../../../core/branch-face';
import { BranchScopeService, WorkingBranch } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { Branch } from '../../../core/models';
import { localizedName } from '../../../core/multilingual';
import { MENU } from '../../../ui/menu.component';

/**
 * The branch block of the back-office header (REDESIGN_PLAN.md §8, D2).
 *
 * - **The Admin** gets the working branch as a menu: every active branch, the central warehouse
 *   marked, then *All branches*. The choice is UI context only (`BranchScopeService`): it starts
 *   the branch filters of the pages that read it, it is kept per user, and it reaches no access
 *   rule. It is not the dashboard's choice, which is kept apart (`dashboard/dashboard-scope.ts`).
 * - **Everyone else** sees the branch they work at as plain text — no chevron, no fake selector —
 *   because the server confines them to it whatever a screen would ask.
 *
 * The list is the device's synced copy of the branches (`BranchFace`), so the menu never waits on
 * a request.
 */
@Component({
  selector: 'app-working-branch',
  standalone: true,
  imports: [...MENU],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (scope.canChoose()) {
      <app-menu align="start" triggerClass="btn-ghost wb__trigger" [label]="i18n.t('scope.working') + ' — ' + current()">
        <span menuTrigger class="wb__text">
          <span class="wb__label">{{ i18n.t('scope.working') }}</span>
          <strong dir="auto">{{ current() }}</strong>
          <span class="wb__chevron" aria-hidden="true">▾</span>
        </span>
        @for (b of branches(); track b.id) {
          <button
            type="button"
            appMenuItem
            [class.is-current]="isCurrent(b.id)"
            [attr.aria-current]="isCurrent(b.id) ? 'true' : null"
            (click)="choose({ kind: 'branch', id: b.id })"
          >
            <span dir="auto">{{ name(b) }}</span>
            @if (b.isWarehouse) {
              <span class="wb__tag">{{ i18n.t(b.isMain ? 'backoffice.branches.mainWarehouse' : 'backoffice.branches.kind.warehouseShort') }}</span>
            } @else if (b.isMain) {
              <span class="wb__tag">{{ i18n.t('backoffice.branches.mainBranch') }}</span>
            }
          </button>
        }
        <hr appMenuSeparator />
        <button
          type="button"
          appMenuItem
          [class.is-current]="isCurrent(null)"
          [attr.aria-current]="isCurrent(null) ? 'true' : null"
          (click)="choose({ kind: 'all' })"
        >
          {{ i18n.t('scope.all') }}
        </button>
      </app-menu>
    } @else {
      <p class="wb__plain">
        <span class="wb__label">{{ i18n.t('branchLock.branch') }}</span>
        <strong dir="auto">{{ face.name() || '—' }}</strong>
      </p>
    }
  `,
  styles: [
    `
      :host {
        display: flex;
        min-inline-size: 0;
      }
      .wb__plain {
        display: flex;
        align-items: baseline;
        gap: var(--sp-2);
        margin: 0;
        min-inline-size: 0;
        font-size: var(--fs-sm);
        color: var(--text);
      }
      .wb__plain strong,
      .wb__text strong {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        font-weight: 600;
        color: var(--text);
      }
      .wb__text {
        display: inline-flex;
        align-items: baseline;
        gap: var(--sp-2);
        min-inline-size: 0;
        max-inline-size: 28ch;
      }
      .wb__label {
        font-size: var(--fs-meta);
        color: var(--muted);
        white-space: nowrap;
      }
      .wb__chevron {
        color: var(--muted);
      }
      .wb__tag {
        margin-inline-start: auto;
        font-size: var(--fs-meta);
        color: var(--muted);
        font-weight: 400;
      }
      :host ::ng-deep .wb__trigger {
        min-block-size: var(--control-h-sm);
        padding: 0 var(--sp-3);
        font-size: var(--fs-sm);
        border: var(--line) solid var(--border);
        border-radius: var(--radius-sm);
        background: var(--surface);
        color: var(--text);
        box-shadow: none;

        &:hover {
          background: var(--surface-2);
          border-color: var(--border);
          transform: none;
          box-shadow: none;
        }

        &:focus-visible {
          outline: 2px solid var(--accent);
          outline-offset: 2px;
        }
      }
      /* A phone keeps the name and drops the label, as the header did before the menu. */
      @media (max-width: 640px) {
        .wb__label {
          display: none;
        }
        .wb__text {
          max-inline-size: 16ch;
        }
      }
    `,
  ],
})
export class WorkingBranchComponent {
  readonly i18n = inject(I18nService);
  readonly scope = inject(BranchScopeService);
  readonly face = inject(BranchFace);

  /** Active places: the selling branches before the warehouses, the main one of each first, then by name. */
  readonly branches = computed(() =>
    this.face
      .all()
      .filter((b) => b.isActive)
      .sort(
        (a, b) =>
          Number(!!a.isWarehouse) - Number(!!b.isWarehouse) ||
          Number(b.isMain) - Number(a.isMain) ||
          this.name(a).localeCompare(this.name(b)),
      ),
  );

  /** What the trigger says: the working branch by name, or *All branches*. */
  readonly current = computed(() => {
    const working = this.scope.working();
    if (working.kind === 'all') return this.i18n.t('scope.all');
    return this.face.nameOf(working.id) || '—';
  });

  isCurrent(id: string | null): boolean {
    const working = this.scope.working();
    return id === null ? working.kind === 'all' : working.kind === 'branch' && working.id === id;
  }

  choose(next: WorkingBranch): void {
    this.scope.set(next);
  }

  name(branch: Branch): string {
    return localizedName(branch, this.i18n.lang());
  }
}
