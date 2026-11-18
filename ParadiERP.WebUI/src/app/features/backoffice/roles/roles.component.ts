import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AdminRoleRow, AdminService, PermissionModule, RoleMember, adminErrorMessage } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { problemCode } from '../../../core/error-message';
import { I18nService } from '../../../core/i18n.service';
import { keyed, mayGive } from '../../../core/landing';
import { RoleScope } from '../../../core/models';
import { ConfirmService } from '../../../ui/confirm.service';
import { deleteRequest, saveRequest } from '../../../ui/confirm-requests';
import { FoldComponent } from '../../../ui/fold.component';
import { MENU } from '../../../ui/menu.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { AdminPagesApi } from '../admin-pages.api';

/** The refusals this page says in its own words; any other comes as the server wrote it. */
const ROLE_PROBLEMS = [
  'role_name_taken', 'role_name_required', 'role_changed', 'admin_role_protected', 'system_role_fixed', 'system_role',
  'role_has_members', 'role_in_history', 'grant_beyond_own', 'scope_beyond_own', 'unknown_permission', 'unknown_role',
];

const SCOPES: readonly RoleScope[] = ['AssignedBranch', 'TillBranch', 'Company'];

/**
 * Administration › Roles & permissions (AUTH-1 Steps 5–6, REDESIGN_PLAN.md §17.10). The list says each role's
 * scope, how many accounts are on it and how many permissions it holds; a role opens at its own address
 * (`admin/roles/<id>`, `admin/roles/new`), so Back and Forward and a link work.
 *
 * <p>The editor offers only what the server would accept, and the server checks it all again (RoleEditor): the
 * Admin role is read-only; the Manager and Cashier roles change only in their permissions; a role with accounts
 * on it keeps its scope and stays on; a permission the reader does not hold is shown, not offered. The sticky
 * line says how many accounts a save changes. The module sections start folded with their count beside the
 * heading: the details, which hold the one required field, start open.</p>
 */
@Component({
  selector: 'app-backoffice-roles',
  standalone: true,
  imports: [FormsModule, FoldComponent, ...PAGE_CHROME, ...MENU, StatusComponent, TableStateComponent, ListFooterComponent],
  template: `
    @if (!editingKey()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="go(['new'])">
          <span aria-hidden="true">+</span> {{ i18n.t('roles.new') }}
        </button>
      </ng-template>

      <!-- The list's filters (owner, 2026-11-17) run on the server over every role: each change is a request. -->
      <ng-template appPageToolbar>
        <div class="ui-toolbar" role="search">
          <div class="ui-filter">
            <label for="rl-f-scope">{{ i18n.t('roles.col.scope') }}</label>
            <select id="rl-f-scope" [ngModel]="scopeFilter()" (ngModelChange)="scopeFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (s of scopes; track s) {
                <option [ngValue]="s">{{ i18n.t('roles.scope.' + s) }}</option>
              }
            </select>
          </div>
          <div class="ui-filter">
            <label for="rl-f-state">{{ i18n.t('common.state') }}</label>
            <select id="rl-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="active">{{ i18n.t('common.active') }}</option>
              <option ngValue="inactive">{{ i18n.t('common.inactive') }}</option>
            </select>
          </div>
          <div class="ui-filter">
            <label for="rl-f-kind">{{ i18n.t('filters.kind') }}</label>
            <select id="rl-f-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="system">{{ i18n.t('roles.system') }}</option>
              <option ngValue="custom">{{ i18n.t('roles.filter.custom') }}</option>
            </select>
          </div>
          @if (filtered()) {
            <div class="ui-toolbar__end">
              <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
            </div>
          }
        </div>
      </ng-template>
    }

    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }

      @if (!editingKey()) {
        <p class="notice info">{{ i18n.t('roles.howItWorks') }}</p>
        <section class="ui-surface" [attr.aria-busy]="!pager.loaded() && !listError()" [attr.aria-label]="i18n.t('nav.page.roles')">
          @if (listError() && !pager.total()) {
            <app-table-state kind="error" [message]="listError()" (retry)="reload()" />
          } @else if (!pager.loaded()) {
            <app-table-state kind="loading" />
          } @else if (!pager.total() && !filtered()) {
            <app-table-state kind="empty" [message]="i18n.t('roles.none')">
              <button type="button" class="btn-primary" (click)="go(['new'])">{{ i18n.t('roles.new') }}</button>
            </app-table-state>
          } @else if (!pager.total()) {
            <app-table-state kind="noResults" (clear)="clearFilters()" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('roles.col.role') }}</th>
                    <th>{{ i18n.t('roles.col.scope') }}</th>
                    <th class="num">{{ i18n.t('roles.col.accounts') }}</th>
                    <th>{{ i18n.t('roles.col.permissions') }}</th>
                    <th>{{ i18n.t('common.state') }}</th>
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (r of pager.rows(); track r.id) {
                    <tr [class.is-muted]="!r.isActive">
                      <td [attr.data-label]="i18n.t('roles.col.role')">
                        <!-- A name and a description are typed in any language: each keeps its own direction. -->
                        <button type="button" class="ui-link" (click)="go([r.id])"><bdi>{{ roleName(r) }}</bdi></button>
                        @if (r.isSystem) {
                          <span class="ui-sub">{{ i18n.t('roles.system') }}</span>
                        } @else if (r.description) {
                          <span class="ui-sub"><bdi>{{ r.description }}</bdi></span>
                        }
                      </td>
                      <td [attr.data-label]="i18n.t('roles.col.scope')">{{ i18n.t('roles.scope.' + r.scope) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('roles.col.accounts')">{{ r.members }}</td>
                      <td [attr.data-label]="i18n.t('roles.col.permissions')">
                        <span class="mono">{{ i18n.t('roles.permissionsOf', { n: r.permissions.length, total: catalogueSize() }) }}</span>
                      </td>
                      <td [attr.data-label]="i18n.t('common.state')">
                        <app-status [tone]="activeTone(r.isActive)">{{ i18n.t(r.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                      </td>
                      <td class="ui-cell-menu">
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + roleName(r)">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem (click)="go([r.id])">{{ i18n.t('roles.open') }}</button>
                          @if (r.deletable) {
                            <hr appMenuSeparator />
                            <button type="button" appMenuItem class="is-danger" [disabled]="busy()" (click)="remove(r)">
                              {{ i18n.t('roles.delete') }}
                            </button>
                          }
                        </app-menu>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
        <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
      } @else if (missing()) {
        <section class="ui-surface">
          <app-table-state kind="error" [message]="i18n.t('roles.notFound')" (retry)="reload()" />
        </section>
        <div><button type="button" class="btn-ghost" (click)="go([])">{{ i18n.t('roles.back') }}</button></div>
      } @else if (ready()) {
        @if (isAdminRole()) {
          <p class="notice info">{{ i18n.t('roles.adminLocked') }}</p>
        } @else if (editing()?.isSystem) {
          <p class="notice info">{{ i18n.t('roles.systemFixed') }}</p>
        }

        <section class="bo-panel">
          <div class="bo-panel__body bo-form bo-form--full">
            <app-fold class="bo-group" [heading]="i18n.t('roles.details')" [open]="true">
              <div class="bo-fields">
                <div class="bo-field" [class.is-invalid]="nameMissing()">
                  <label for="rl-name">{{ i18n.t('roles.name') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                  <input
                    id="rl-name"
                    dir="auto"
                    maxlength="80"
                    [ngModel]="name()"
                    (ngModelChange)="name.set($event)"
                    (blur)="nameTouched.set(true)"
                    [disabled]="!detailsEditable()"
                    [attr.aria-invalid]="nameMissing() ? 'true' : null"
                    [attr.aria-describedby]="nameMissing() ? 'rl-name-error' : null"
                  />
                  @if (nameMissing()) {
                    <span id="rl-name-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
                <div class="bo-field">
                  <label for="rl-scope">{{ i18n.t('roles.scope') }}</label>
                  <select id="rl-scope" [ngModel]="scope()" (ngModelChange)="scope.set($event)" [disabled]="scopeLocked()" aria-describedby="rl-scope-hint">
                    @for (s of scopes; track s) {
                      <option [ngValue]="s" [disabled]="s === 'Company' && !companyScoped() && scope() !== 'Company'">{{ i18n.t('roles.scope.' + s) }}</option>
                    }
                  </select>
                  <p id="rl-scope-hint" class="bo-hint">
                    {{ i18n.t('roles.scope.' + scope() + '.hint') }}
                    @if (detailsEditable() && memberCount() && !isNew()) {
                      {{ i18n.t('roles.scope.lockedMembers') }}
                    }
                  </p>
                </div>
                <div class="bo-field bo-field--wide">
                  <label for="rl-description">{{ i18n.t('roles.description') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                  <input id="rl-description" dir="auto" maxlength="400" [ngModel]="description()" (ngModelChange)="description.set($event)" [disabled]="!detailsEditable()" />
                </div>
                <div class="bo-field bo-field--wide">
                  <div class="bo-check">
                    <input id="rl-active" type="checkbox" [ngModel]="active()" (ngModelChange)="active.set($event)" [disabled]="activeLocked()" />
                    <label for="rl-active">{{ i18n.t('roles.active') }}</label>
                  </div>
                  @if (detailsEditable() && activeLocked()) {
                    <p class="bo-hint">{{ i18n.t('roles.active.lockedMembers') }}</p>
                  }
                </div>
              </div>
            </app-fold>

            @for (m of catalogue(); track m.module) {
              <app-fold class="bo-group" [heading]="i18n.t('perm.module.' + m.module)">
                <span foldState class="ui-sub mono rl-count">{{ i18n.t('roles.held', { n: heldIn(m), total: m.keys.length }) }}</span>
                @if (permissionsEditable()) {
                  <div class="ui-actionbar rl-bulk">
                    <button type="button" class="btn-ghost bo-small" (click)="setAll(m, true)">{{ i18n.t('roles.selectAll') }}</button>
                    <button type="button" class="btn-ghost bo-small" (click)="setAll(m, false)">{{ i18n.t('roles.clear') }}</button>
                  </div>
                }
                <div class="rl-keys">
                  @for (k of m.keys; track k) {
                    <div class="bo-check">
                      <input
                        type="checkbox"
                        [id]="'rl-' + k"
                        [checked]="keys().has(k)"
                        [disabled]="!permissionsEditable() || !mayChange(k)"
                        (change)="toggle(k, $event)"
                      />
                      <label [for]="'rl-' + k">
                        {{ i18n.t('perm.' + k) }}
                        @if (permissionsEditable() && !mayChange(k)) {
                          <span class="ui-sub">{{ i18n.t('roles.notYours') }}</span>
                        }
                      </label>
                    </div>
                  }
                </div>
              </app-fold>
            }

            @if (!isNew()) {
              <app-fold class="bo-group" [heading]="i18n.t('roles.members')">
                <span foldState class="ui-sub mono rl-count">{{ memberCount() }}</span>
                @if (!members().length) {
                  <p class="bo-hint">{{ i18n.t('roles.membersNone') }}</p>
                } @else {
                  <ul class="rl-members">
                    @for (u of members(); track u.id) {
                      <li [class.is-muted]="!u.isActive">
                        {{ u.fullName }} <span class="ui-sub mono"><bdi dir="ltr">{{ u.signIn }}</bdi></span>
                        @if (!u.isActive) {
                          <app-status [tone]="activeTone(false)">{{ i18n.t('common.inactive') }}</app-status>
                        }
                      </li>
                    }
                  </ul>
                }
              </app-fold>
            }
          </div>
        </section>

        <div class="ui-actionbar ui-actionbar--sticky rl-foot">
          <p class="rl-impact" role="status">
            {{ isNew() || !memberCount() ? i18n.t('roles.impactNone') : i18n.t('roles.impact', { n: memberCount() }) }}
          </p>
          <button type="button" class="btn-ghost" (click)="go([])">{{ i18n.t(isAdminRole() ? 'roles.back' : 'common.cancel') }}</button>
          @if (!isAdminRole()) {
            <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">{{ i18n.t('common.save') }}</button>
          }
        </div>
      } @else {
        <section class="ui-surface"><app-table-state kind="loading" /></section>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .rl-keys {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(min(100%, 18rem), 1fr));
        gap: var(--sp-2) var(--sp-4);
      }
      /* A long name wraps on a phone: the box grows with it rather than letting it spill onto the next. */
      .rl-keys .bo-check {
        block-size: auto;
      }
      .rl-bulk {
        justify-content: flex-start;
        margin-block-end: var(--sp-2);
      }
      .rl-count {
        white-space: nowrap;
      }
      .rl-members {
        display: grid;
        gap: var(--sp-2);
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .rl-members li {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: var(--sp-2);
      }
      .rl-foot {
        align-items: center;
      }
      .rl-impact {
        margin: 0;
        margin-inline-end: auto;
        color: var(--text-2);
      }
    `,
  ],
})
export class BackofficeRolesComponent {
  private readonly admin = inject(AdminService);
  private readonly pages = inject(AdminPagesApi);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  private readonly router = inject(Router);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** The address under the page (core/nav.ts `deep`): `[]`, `[id]` or `['new']`. */
  readonly path = input<string[]>([]);

  readonly scopes = SCOPES;

  // The list's filters (owner, 2026-11-17), applied on the server over every role.
  readonly scopeFilter = signal<'all' | RoleScope>('all');
  readonly stateFilter = signal<'all' | 'active' | 'inactive'>('all');
  readonly kindFilter = signal<'all' | 'system' | 'custom'>('all');
  readonly filtered = computed(() => this.scopeFilter() !== 'all' || this.stateFilter() !== 'all' || this.kindFilter() !== 'all');
  /**
   * The roles a page at a time from the server (owner, 2026-11-17: "paginate in backend"), the system roles first and
   * then the company's by name. Asked for while the list is on screen; a role opened is read by its id.
   */
  readonly pager = createServerPager(
    (page) => this.pages.rolesPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'admin.roles',
      filters: () => ({ scope: this.scopeFilter(), state: this.stateFilter(), kind: this.kindFilter() }),
      enabled: () => !this.editingKey(),
    },
  );
  /** The page on screen. */
  readonly roles = computed(() => this.pager.rows());

  clearFilters(): void {
    this.scopeFilter.set('all');
    this.stateFilter.set('all');
    this.kindFilter.set('all');
  }
  readonly catalogue = signal<PermissionModule[]>([]);
  readonly members = signal<RoleMember[]>([]);
  readonly busy = signal(false);
  /** The permission catalogue has been read, or refused. */
  private readonly catalogueRead = signal(false);
  /** The role the editor's address names, read by its id (whatever page it is on), with the key it was read for. */
  private readonly opened = signal<{ key: string; role: AdminRoleRow | null } | null>(null);
  readonly error = signal<string | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  /** The list or the catalogue could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return this.loadError() ?? (e ? adminErrorMessage(e) : null);
  });

  // The editor's fields.
  readonly name = signal('');
  readonly description = signal('');
  readonly scope = signal<RoleScope>('AssignedBranch');
  readonly active = signal(true);
  readonly keys = signal<ReadonlySet<string>>(new Set());
  readonly nameTouched = signal(false);
  private version: number | undefined;

  readonly editingKey = computed(() => this.path()[0] ?? null);
  readonly isNew = computed(() => this.editingKey() === 'new');
  /** Ready to draw: the catalogue is in, and the role the address names has been read (or found missing). */
  readonly loaded = computed(() => {
    const key = this.editingKey();
    return this.catalogueRead() && (!key || key === 'new' || this.opened()?.key === key);
  });
  readonly editing = computed(() => {
    if (this.isNew()) return null;
    const opened = this.opened();
    return opened && opened.key === this.editingKey() ? opened.role : null;
  });
  readonly missing = computed(() => this.loaded() && !this.isNew() && !!this.editingKey() && !this.editing());
  readonly ready = computed(() => this.loaded() && (this.isNew() || !!this.editing()));
  readonly isAdminRole = computed(() => this.editing()?.key === 'admin');
  readonly detailsEditable = computed(() => this.isNew() || !!this.editing()?.detailsEditable);
  readonly permissionsEditable = computed(() => this.isNew() || !!this.editing()?.permissionsEditable);
  /** The accounts on the role, as the list counted them: whose access a save changes, and what locks its scope. */
  readonly memberCount = computed(() => this.editing()?.members ?? 0);
  readonly scopeLocked = computed(() => !this.detailsEditable() || (!this.isNew() && this.memberCount() > 0));
  readonly activeLocked = computed(() => !this.detailsEditable() || (!this.isNew() && this.memberCount() > 0 && !!this.editing()?.isActive));
  readonly catalogueSize = computed(() => this.catalogue().reduce((n, m) => n + m.keys.length, 0));
  readonly nameMissing = computed(() => this.nameTouched() && !this.name().trim());
  readonly companyScoped = computed(() => {
    const user = this.auth.currentUser();
    return !!user && (keyed(user) ? user.scope === 'Company' : user.role === 'Admin');
  });

  constructor() {
    void this.loadCatalogue();
    // The role the address names is read by its id: the list's page may not hold it.
    effect(() => {
      const key = this.editingKey();
      untracked(() => {
        if (!key || key === 'new') {
          if (!key) this.opened.set(null);
          return;
        }
        if (this.opened()?.key !== key) void this.open(key);
      });
    });
    // The editor follows the address: opening a role fills the form from it, and the header names it.
    effect(() => {
      const key = this.editingKey();
      const role = this.editing();
      const loaded = this.loaded();
      untracked(() => {
        if (!key) {
          this.chrome?.heading.set(null);
          this.chrome?.trail.set([]);
          return;
        }
        if (!loaded) return;
        this.fill(role);
        const title = role ? this.roleName(role) : this.i18n.t('roles.new');
        this.chrome?.heading.set(title);
        this.chrome?.trail.set([{ label: title }]);
      });
    });
  }

  /** Read everything again: the catalogue, and the list on screen or the role the address names. */
  async reload(): Promise<void> {
    const key = this.editingKey();
    await Promise.all([
      this.loadCatalogue(),
      !key ? this.pager.reload() : key !== 'new' ? this.open(key) : Promise.resolve(),
    ]);
  }

  private async loadCatalogue(): Promise<void> {
    this.busy.set(true);
    this.loadError.set(null);
    try {
      this.catalogue.set(await this.admin.listPermissions());
    } catch (e) {
      this.loadError.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
      this.catalogueRead.set(true);
    }
  }

  /** One role by its id, as the list would show it; none (said as "not found") when it cannot be read. */
  private async open(key: string): Promise<void> {
    let role: AdminRoleRow | null = null;
    try {
      role = (await this.pages.rolesPage({ skip: 0, take: 1, id: key })).items[0] ?? null;
    } catch {
      role = null;
    }
    if (this.editingKey() === key) this.opened.set({ key, role });
  }

  go(segments: string[]): void {
    this.error.set(null);
    this.notice.set(null);
    void this.router.navigate(['/backoffice', 'admin', 'roles', ...segments]);
  }

  /** A system role in the reader's language; a role the company made, by the name it was given. */
  roleName(role: AdminRoleRow): string {
    return role.key ? this.i18n.t(`backoffice.users.role.${role.key}`) : role.name;
  }

  activeTone(active: boolean) {
    return toneOf('active', active ? 'Active' : 'Inactive');
  }

  heldIn(module: PermissionModule): number {
    return module.keys.filter((k) => this.keys().has(k)).length;
  }

  /** A key the reader holds, or covers — never more than their own authority (RoleEditor on the server). */
  mayChange(key: string): boolean {
    return mayGive(this.auth.currentUser(), key);
  }

  toggle(key: string, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    const next = new Set(this.keys());
    if (on) next.add(key);
    else next.delete(key);
    this.keys.set(next);
  }

  /** Every key of the module the reader may change, on or off; the others stay as they are. */
  setAll(module: PermissionModule, on: boolean): void {
    const next = new Set(this.keys());
    for (const k of module.keys.filter((key) => this.mayChange(key))) {
      if (on) next.add(k);
      else next.delete(k);
    }
    this.keys.set(next);
  }

  async save(): Promise<void> {
    this.nameTouched.set(true);
    if (this.detailsEditable() && !this.name().trim()) return;
    const role = this.editing();
    if (!(await this.confirm.ask(saveRequest(this.i18n, role ? this.roleName(role) : this.name().trim())))) return;
    this.busy.set(true);
    this.error.set(null);
    const permissions = this.catalogue().flatMap((m) => m.keys).filter((k) => this.keys().has(k));
    try {
      if (!role) {
        const created = await this.admin.createRole({
          name: this.name().trim(), description: this.description().trim() || null, scope: this.scope(), isActive: this.active(), permissions,
        });
        // The editor opens on the role as the server answered it; the list is read again for the way back.
        this.opened.set({ key: created.id, role: created });
        void this.pager.reload();
        this.go([created.id]);
        this.notice.set(this.i18n.t('roles.created'));
        return;
      }
      const saved = await this.admin.updateRole(role.id, role.detailsEditable
        ? { name: this.name().trim(), description: this.description().trim() || null, scope: this.scope(), isActive: this.active(), permissions, version: this.version }
        : { permissions, version: this.version });
      this.opened.set({ key: saved.id, role: saved });
      void this.pager.reload();
      this.version = saved.version;
      this.notice.set(this.i18n.t('roles.saved'));
    } catch (e) {
      this.error.set(this.problemMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async remove(role: AdminRoleRow): Promise<void> {
    const ok = await this.confirm.ask(deleteRequest(this.i18n, this.i18n.t('roles.delete'), this.i18n.t('roles.deleteConfirm', { name: this.roleName(role) })));
    if (!ok) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.admin.deleteRole(role.id);
      await this.pager.reload();
      this.notice.set(this.i18n.t('roles.deleted'));
    } catch (e) {
      this.error.set(this.problemMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  private fill(role: AdminRoleRow | null): void {
    this.nameTouched.set(false);
    this.members.set([]);
    this.name.set(role ? role.name : '');
    // A system role's description is the application's, said in the reader's language; the stored one is English.
    this.description.set(role?.key ? this.i18n.t(`roles.describe.${role.key}`) : (role?.description ?? ''));
    this.scope.set(role?.scope ?? 'AssignedBranch');
    this.active.set(role?.isActive ?? true);
    this.keys.set(new Set(role?.permissions ?? []));
    this.version = role?.version;
    if (role) {
      this.admin.roleMembers(role.id).then(
        (members) => {
          if (this.editing()?.id === role.id) this.members.set(members);
        },
        // The list keeps its count; members it could not read leave the editor's lock to the server.
        () => this.members.set([]),
      );
    }
  }

  private problemMessage(error: unknown): string {
    const code = problemCode(error);
    return code && ROLE_PROBLEMS.includes(code) ? this.i18n.t('roles.error.' + code) : adminErrorMessage(error);
  }
}
