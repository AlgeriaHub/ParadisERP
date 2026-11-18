import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { Employee, HrApi } from './payroll/hr-api.service';
import {
  ADMIN_ROLE_PERMISSIONS,
  AdminBranch,
  AdminRoleRow,
  AdminRole,
  AdminService,
  AdminUser,
  CreateUserRequest,
  PermissionModule,
  UpdateUserRequest,
  adminErrorMessage,
} from '../../core/admin.service';
import { ApiError } from '../../core/api.service';
import { AuthService, MIN_PASSWORD_LENGTH } from '../../core/auth.service';
import { I18nService } from '../../core/i18n.service';
import { mayGive } from '../../core/landing';
import { localizedName } from './backoffice-format';
import { isolate } from '../../core/bidi';
import { TouchedFields } from './backoffice-forms';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest, deleteRequest, saveRequest } from '../../ui/confirm-requests';
import { FoldComponent } from '../../ui/fold.component';
import { MENU } from '../../ui/menu.component';
import { StatusComponent } from '../../ui/status.component';
import { StatusTone, toneOf } from '../../ui/status-tones';
import { TableStateComponent } from '../../ui/table-state.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { PAGE_CHROME } from './shell/page-chrome';
import { AdminPagesApi } from './admin-pages.api';
import { PasswordVisibilityDirective } from '../../ui/password-visibility.directive';

type CredentialKind = 'password' | 'pin';

/** The server's refusal to place a cashier on a warehouse (CashierTills.CashierOnWarehouseCode). */
const CASHIER_ON_WAREHOUSE = 'cashier_on_warehouse';

/** The permission refusals the roles screen already says in words (RoleEditor, UserGrantEditor). */
const PERMISSION_PROBLEMS = ['grant_beyond_own', 'scope_beyond_own', 'unknown_permission', 'unknown_role'];

/** A save made on an older view of the account's role or grants (UserGrantEditor.UserChangedCode). */
const USER_CHANGED = 'user_changed';

/**
 * The keys whose consequences the screen has always spelled out beneath them: the five that were the screen's own
 * ticks, and "sell untaxed at the till".
 */
const KEY_HINTS: Readonly<Record<string, string>> = {
  'stock.view_other_branches': 'backoffice.users.perm.stock.view_other_branches.hint',
  'payroll.manage': 'backoffice.users.perm.payroll.manage.hint',
  'hr.attendance': 'backoffice.users.perm.hr.attendance.hint',
  'payroll.edit_statutory': 'backoffice.users.perm.payroll.edit_statutory.hint',
  'treasury.cash_box': 'backoffice.users.perm.treasury.cash_box.hint',
  'sales.untaxed': 'backoffice.users.sellsUntaxed.hint',
};

/** The account refusals the server names (Infrastructure/Auth/EmployeeAccounts), each with its own sentence here. */
const ACCOUNT_PROBLEMS = [
  'employee_required',
  'employee_unknown',
  'employee_inactive',
  'employee_has_account',
  'account_already_linked',
  'employee_holds_last_admin',
];

/** The stable `code` of a ProblemDetails refusal; the transport hands the body back as the message. */
function problemCode(error: unknown): string | null {
  if (!(error instanceof ApiError)) return null;
  try {
    const parsed = JSON.parse(error.message) as { code?: unknown };
    return typeof parsed?.code === 'string' ? parsed.code : null;
  } catch {
    return null;
  }
}

/** The three system roles' fixed ids (Domain/Authorization/Roles.cs SystemRoles). */
const ADMIN_ROLE_ID = '5a17e000-0000-4000-8000-0000000000a1';
const MANAGER_ROLE_ID = '5a17e000-0000-4000-8000-0000000000a2';
const CASHIER_ROLE_ID = '5a17e000-0000-4000-8000-0000000000a3';
const SYSTEM_ROLE_IDS: readonly string[] = [ADMIN_ROLE_ID, MANAGER_ROLE_ID, CASHIER_ROLE_ID];
const ROLE_ID_OF: Readonly<Record<AdminRole, string>> = { Admin: ADMIN_ROLE_ID, Manager: MANAGER_ROLE_ID, Cashier: CASHIER_ROLE_ID };

/**
 * The system roles as this screen showed them before roles came from the server: what it always showed each one
 * holding of the ticks it offers (the Admin's four legacy grants; "sell untaxed" with the Admin and the Manager).
 */
const LEGACY_ROLES: AdminRoleRow[] = (
  [
    [ADMIN_ROLE_ID, 'admin', 'Company', [...ADMIN_ROLE_PERMISSIONS, 'sales.untaxed']],
    [MANAGER_ROLE_ID, 'manager', 'AssignedBranch', ['sales.untaxed']],
    [CASHIER_ROLE_ID, 'cashier', 'TillBranch', []],
  ] as const
).map(([id, key, scope, permissions]) => ({
  id, key, name: key, description: null, isActive: true, isSystem: true, scope, version: 0, permissions: [...permissions],
  members: 0, permissionsEditable: false, detailsEditable: false, deletable: false,
}));

@Component({
  selector: 'app-backoffice-users',
  standalone: true,
  imports: [
    FormsModule,
    FoldComponent,
    PasswordVisibilityDirective,
    ...PAGE_CHROME,
    ...MENU,
    StatusComponent,
    TableStateComponent,
    ListFooterComponent,
  ],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-primary" (click)="openCreate()">
        <span aria-hidden="true">+</span> {{ i18n.t('backoffice.users.new') }}
      </button>
    </ng-template>

    <!-- The list's filters (owner, 2026-11-17) run on the server over every account: each change is a request. -->
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="us-f-role">{{ i18n.t('backoffice.users.role') }}</label>
          <select id="us-f-role" [ngModel]="roleFilter()" (ngModelChange)="roleFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (option of roleOptions(); track option.value) {
              <option [ngValue]="option.value">{{ option.label }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="us-f-branch">{{ i18n.t('backoffice.users.branch') }}</label>
          <select id="us-f-branch" [ngModel]="branchFilter()" (ngModelChange)="branchFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (b of branches(); track b.id) {
              <option [ngValue]="b.id">{{ name(b) }}</option>
            }
            <option ngValue="none">{{ i18n.t('backoffice.users.branch.none') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="us-f-state">{{ i18n.t('common.state') }}</label>
          <select id="us-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="active">{{ i18n.t('common.active') }}</option>
            <option ngValue="inactive">{{ i18n.t('common.inactive') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="us-f-employee">{{ i18n.t('backoffice.users.employee') }}</label>
          <select id="us-f-employee" [ngModel]="linkFilter()" (ngModelChange)="linkFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="linked">{{ i18n.t('users.filter.linked') }}</option>
            <option ngValue="unlinked">{{ i18n.t('backoffice.users.notLinked') }}</option>
          </select>
        </div>
        @if (filtered()) {
          <div class="ui-toolbar__end">
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          </div>
        }
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }

      @if (editorOpen()) {
        <div class="bo-backdrop" (click)="cancelEdit()"></div>
      }
      <section
        class="bo-panel"
        [class.bo-edit-modal]="editorOpen()"
        [class.bo-editor-hidden]="!editorOpen()"
        [attr.role]="editorOpen() ? 'dialog' : null"
        [attr.aria-modal]="editorOpen() ? 'true' : null"
        [attr.aria-label]="editorOpen() ? (editingId() ? i18n.t('common.edit') : i18n.t('backoffice.users.new')) : null"
        (keydown.escape)="editorOpen() && cancelEdit()"
      >
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ editingId() ? i18n.t('common.edit') : i18n.t('backoffice.users.new') }}</h2>
        </div>
        <div class="bo-panel__body bo-form">
          <app-fold class="bo-group" [heading]="i18n.t('common.group.identity')" [open]="true">
            <div class="bo-fields">
              @if (!editingId()) {
                <!-- An account is given to an employee (2026-10-05): chosen here, never typed. The
                     account takes the employee's name, and the server draws its Login ID on save. -->
                <div class="bo-field bo-field--wide" [class.is-invalid]="employeeMissing()">
                  <label for="us-employee">
                    {{ i18n.t('backoffice.users.employee') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <select
                    id="us-employee"
                    [(ngModel)]="employeeId"
                    (ngModelChange)="employeeChosen($event)"
                    (blur)="touched.touch('employeeId')"
                    [attr.aria-invalid]="employeeMissing() ? 'true' : null"
                    [attr.aria-describedby]="employeeMissing() ? 'us-employee-error' : 'us-employee-hint'"
                  >
                    <option [ngValue]="null" disabled>{{ i18n.t('backoffice.users.employee.choose') }}</option>
                    @for (e of available(); track e.id) {
                      <option [ngValue]="e.id">{{ e.fullName }} — {{ isolate(e.number) }}</option>
                    }
                  </select>
                  @if (employeeMissing()) {
                    <span id="us-employee-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                  @if (!available().length) {
                    <p class="bo-hint">{{ i18n.t('backoffice.users.employee.none') }}</p>
                  }
                  <p id="us-employee-hint" class="bo-hint">{{ i18n.t('backoffice.users.loginId.willGenerate') }}</p>
                </div>
              } @else {
                <div class="bo-field">
                  <p class="bo-fixed-label">
                    {{ i18n.t(editingLoginId ? 'backoffice.users.loginId' : 'backoffice.users.username') }}
                  </p>
                  <p class="bo-fixed mono">{{ editingLoginId || username }}</p>
                  @if (!editingLoginId) {
                    <span class="bo-hint">{{ i18n.t('backoffice.users.legacyName') }}</span>
                  }
                </div>
                @if (editingEmployeeId) {
                  <div class="bo-field">
                    <p class="bo-fixed-label">{{ i18n.t('backoffice.users.fullName') }}</p>
                    <p class="bo-fixed">{{ fullName }}</p>
                    <span class="bo-hint">{{ i18n.t('backoffice.users.fullName.fromEmployee') }}</span>
                  </div>
                } @else {
                  <div class="bo-field" [class.is-invalid]="touched.missing('fullName', fullName)">
                    <label for="us-fullname">
                      {{ i18n.t('backoffice.users.fullName') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <input
                      id="us-fullname" [(ngModel)]="fullName"
                      [attr.aria-invalid]="touched.missing('fullName', fullName) ? 'true' : null"
                      [attr.aria-describedby]="touched.missing('fullName', fullName) ? 'us-fullname-error' : null"
                      (blur)="touched.touch('fullName')"
                    />
                    @if (touched.missing('fullName', fullName)) {
                      <span id="us-fullname-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                    }
                  </div>
                  <!-- An account from before the rule: linked to its employee in place. How it signs in
                       does not change; it takes the employee's name and follows them from now on. -->
                  <div class="bo-field bo-field--wide">
                    <label for="us-link">{{ i18n.t('backoffice.users.link') }}</label>
                    <select id="us-link" [(ngModel)]="linkEmployeeId" aria-describedby="us-link-hint">
                      <option [ngValue]="null">{{ i18n.t('backoffice.users.employee.choose') }}</option>
                      @for (e of available(); track e.id) {
                        <option [ngValue]="e.id">{{ e.fullName }} — {{ isolate(e.number) }}</option>
                      }
                    </select>
                    <p id="us-link-hint" class="bo-hint">{{ i18n.t('backoffice.users.link.hint') }}</p>
                    <div>
                      <button type="button" class="btn-ghost bo-small" [disabled]="!linkEmployeeId || busy()" (click)="link()">
                        {{ i18n.t('backoffice.users.link.action') }}
                      </button>
                    </div>
                  </div>
                }
              }
              <div class="bo-field">
                <label for="us-role">{{ i18n.t('backoffice.users.role') }}</label>
                <!-- Every role the company has (AUTH-1 Step 5), the system three first; a role switched off is
                     offered to nobody new, and stays shown for an account already on it. -->
                <select id="us-role" [ngModel]="roleId" (ngModelChange)="chooseRole($event)" [disabled]="editingSelf()">
                  @for (r of offeredRoles(); track r.id) {
                    <option [ngValue]="r.id">{{ roleRowLabel(r) }}{{ r.isActive ? '' : ' — ' + i18n.t('common.inactive') }}</option>
                  }
                </select>
              </div>
              <!-- Every user works at a branch, whatever the role (BranchLock): required here and
                   on the server. For everyone but the Admin it is the only branch they will see. -->
              <div class="bo-field" [class.is-invalid]="branchMissing()">
                <label for="us-branch">
                  {{ i18n.t('backoffice.users.branch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <select
                  id="us-branch"
                  [(ngModel)]="branchId"
                  (blur)="touched.touch('branchId')"
                  [attr.aria-invalid]="branchMissing() ? 'true' : null"
                  [attr.aria-describedby]="branchMissing() ? 'us-branch-error' : null"
                >
                  <option [ngValue]="null" disabled>{{ i18n.t('backoffice.users.branch.choose') }}</option>
                  @for (b of branches(); track b.id) {
                    <!-- A cashier sells, and a warehouse does not: listed, not chosen, with why. -->
                    <option [ngValue]="b.id" [disabled]="role === 'Cashier' && b.isWarehouse">
                      {{ name(b) }}{{ role === 'Cashier' && b.isWarehouse ? ' — ' + i18n.t('backoffice.users.branch.warehouseNoCashier') : '' }}
                    </option>
                  }
                </select>
                @if (branchMissing()) {
                  <span id="us-branch-error" class="bo-error" role="alert">{{
                    i18n.t(editingId() ? 'backoffice.users.branch.legacy' : 'common.fieldRequired')
                  }}</span>
                } @else {
                  <p class="bo-hint">
                    {{ i18n.t(role === 'Admin' ? 'backoffice.users.branch.hintAdmin' : 'backoffice.users.branch.hintLocked') }}
                  </p>
                  @if (role === 'Cashier') {
                    <p class="bo-hint">{{ i18n.t('backoffice.users.branch.hintTill') }}</p>
                  }
                }
              </div>
              @if (editingId()) {
                <div class="bo-check">
                  <input id="us-active" type="checkbox" [(ngModel)]="isActive" [disabled]="editingSelf()" />
                  <label for="us-active">{{ i18n.t('common.active') }}</label>
                </div>
              }
            </div>
          </app-fold>

          <!-- What the account may do (AUTH-1 §17.10): its role's permissions, ticked and locked, and any key given to
               this person on top of it, from any module. Folded at first: the role is what most accounts need. Each
               module folds on its own, its count beside the heading, so the summary reads without opening it. -->
          <app-fold class="bo-group" [heading]="i18n.t('backoffice.users.permissions')">
            <span foldState class="ui-sub mono us-count">{{ i18n.t('roles.held', { n: effectiveCount(), total: catalogueSize() }) }}</span>
            <p class="bo-group__note" foldLead>{{ i18n.t('backoffice.users.permissions.hint') }}</p>
            <div class="us-perms-body">
              <p class="us-summary" role="status">{{ summary() }}</p>
              @if (ownKeys().length) {
                <p class="bo-hint">{{ i18n.t('users.perm.ownList', { list: namesOf(ownKeys()) }) }}</p>
              }
              @if (role === 'Cashier') {
                <p class="bo-hint">{{ i18n.t('users.perm.tillScope') }}</p>
              }
              @if (!catalogue().length) {
                <p class="notice warning" role="status">{{ i18n.t('users.perm.unavailable') }}</p>
              }
              @for (m of catalogue(); track m.module) {
                <app-fold class="bo-group us-module" [heading]="i18n.t('perm.module.' + m.module)">
                  <span foldState class="ui-sub mono us-count">
                    {{ i18n.t('roles.held', { n: heldIn(m), total: m.keys.length }) }}
                    @if (ownIn(m); as own) {
                      · {{ i18n.t('users.perm.ownCount', { n: own }) }}
                    }
                  </span>
                  <div class="us-keys">
                    @for (k of m.keys; track k) {
                      <div class="us-key">
                        <div class="bo-check">
                          <input
                            type="checkbox"
                            [id]="'us-perm-' + k"
                            [checked]="held(k)"
                            [disabled]="byRole(k) || !mayChange(k)"
                            [attr.aria-describedby]="hintOf(k) ? 'us-perm-' + k + '-hint' : null"
                            (change)="toggle(k, $event)"
                          />
                          <label [for]="'us-perm-' + k">
                            {{ i18n.t('perm.' + k) }}
                            @if (byRole(k)) {
                              <span class="ui-sub">{{ i18n.t('backoffice.users.permissions.byRole') }}</span>
                            } @else if (extras().has(k)) {
                              <span class="ui-sub">{{ i18n.t('users.perm.own') }}</span>
                            } @else if (!mayChange(k)) {
                              <span class="ui-sub">{{ i18n.t('roles.notYours') }}</span>
                            }
                          </label>
                        </div>
                        @if (hintOf(k); as hint) {
                          <p class="bo-hint" [id]="'us-perm-' + k + '-hint'">{{ i18n.t(hint) }}</p>
                        }
                      </div>
                    }
                  </div>
                </app-fold>
              }
            </div>
          </app-fold>

          @if (!editingId()) {
            <app-fold class="bo-group" [heading]="i18n.t('login.title')" [open]="true">
              <p class="bo-group__note" foldLead>{{ i18n.t('backoffice.users.pinHint') }}</p>
              <div class="bo-fields">
                <div class="bo-field" [class.is-invalid]="passwordShort()">
                  <label for="us-password">
                    {{ i18n.t('backoffice.users.password') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="us-password"
                    type="password"
                    autocomplete="new-password"
                    [(ngModel)]="password"
                    [attr.aria-invalid]="passwordShort() ? 'true' : null"
                    [attr.aria-describedby]="passwordShort() ? 'us-password-error' : null"
                    (blur)="touched.touch('password')"
                  />
                  @if (passwordShort()) {
                    <span id="us-password-error" class="bo-error" role="alert">
                      {{ i18n.t('backoffice.users.passwordMin', { n: minPassword }) }}
                    </span>
                  }
                </div>
                <div class="bo-field">
                  <label for="us-pin">
                    {{ i18n.t('backoffice.users.pin') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                  </label>
                  <input id="us-pin" class="mono" [(ngModel)]="pin" inputmode="numeric" />
                </div>
              </div>
            </app-fold>
          }
        </div>
        <div class="bo-panel__foot">
          <button type="button" class="btn-ghost" (click)="cancelEdit()">{{ i18n.t('common.cancel') }}</button>
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">
            {{ i18n.t('common.save') }}
          </button>
        </div>
      </section>

      <section class="ui-surface" [attr.aria-busy]="!pager.loaded()" [attr.aria-label]="i18n.t('backoffice.tabs.users')">
        @if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('backoffice.users.none')">
            <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('backoffice.users.new') }}</button>
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('backoffice.users.loginId') }}</th>
                  <th>{{ i18n.t('backoffice.users.fullName') }}</th>
                  <th>{{ i18n.t('backoffice.users.role') }}</th>
                  <th>{{ i18n.t('backoffice.users.branch') }}</th>
                  <th>{{ i18n.t('backoffice.users.till') }}</th>
                  <th>{{ i18n.t('backoffice.users.permissions') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (u of pager.rows(); track u.id) {
                  <tr [class.is-muted]="!u.isActive">
                    <td [attr.data-label]="i18n.t('backoffice.users.loginId')">
                      @if (u.loginId) {
                        <span class="mono">{{ u.loginId }}</span>
                      } @else {
                        <!-- An account from before Login IDs signs in with its username, shown as such. -->
                        <span class="mono">{{ u.username }}</span>
                        <span class="ui-sub">{{ i18n.t('backoffice.users.noLoginId') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('backoffice.users.fullName')">
                      <button type="button" class="ui-link" (click)="edit(u)">{{ u.fullName }}</button>
                      @if (u.employeeNumber) {
                        <span class="ui-sub mono"><bdi dir="ltr">{{ u.employeeNumber }}</bdi></span>
                      } @else {
                        <span class="us-unlinked"><app-status tone="warning">{{ i18n.t('backoffice.users.notLinked') }}</app-status></span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('backoffice.users.role')">{{ userRoleLabel(u) }}</td>
                    <td [attr.data-label]="i18n.t('backoffice.users.branch')">{{ branchLabel(u) }}</td>
                    <td class="mono us-till" [attr.data-label]="i18n.t('backoffice.users.till')">{{ tillLabel(u) }}</td>
                    <td class="us-perms" [attr.data-label]="i18n.t('backoffice.users.permissions')">
                      <span class="us-perms__text" [title]="permissionsLabel(u)">{{ permissionsLabel(u) }}</span>
                    </td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="activeTone(u.isActive)">{{ i18n.t(u.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                      @if (u.twoFactorOn) {
                        <app-status [tone]="twoFactorTone" data-two-factor-on>{{ i18n.t('backoffice.users.twoFactorOn') }}</app-status>
                      }
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + u.fullName">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="edit(u)">{{ i18n.t('common.edit') }}</button>
                        @if (!u.employeeId) {
                          <button type="button" appMenuItem (click)="edit(u)">{{ i18n.t('backoffice.users.link') }}</button>
                        }
                        <hr appMenuSeparator />
                        <button type="button" appMenuItem (click)="openCredential(u, 'password')">{{ i18n.t('backoffice.users.resetPassword') }}</button>
                        <button type="button" appMenuItem (click)="openCredential(u, 'pin')">{{ i18n.t('backoffice.users.resetPin') }}</button>
                        <button type="button" appMenuItem [disabled]="busy()" (click)="revokeSessions(u)">{{ i18n.t('backoffice.users.revokeSessions') }}</button>
                        @if (u.twoFactorOn) {
                          <button type="button" appMenuItem [disabled]="busy()" (click)="resetTwoFactor(u)" data-two-factor-reset>
                            {{ i18n.t('backoffice.users.twoFactorReset') }}
                          </button>
                        }
                        <hr appMenuSeparator />
                        <button
                          type="button"
                          appMenuItem
                          class="is-danger"
                          [disabled]="isSelf(u)"
                          [title]="isSelf(u) ? i18n.t('backoffice.users.cannotDeactivateSelf') : ''"
                          (click)="deactivate(u)"
                        >
                          {{ i18n.t('backoffice.users.deactivate') }}
                        </button>
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
    </div>

    @if (credentialTarget(); as target) {
      <div class="bo-backdrop" (click)="credentialTarget.set(null)">
        <div class="bo-modal bo-modal--narrow" role="dialog" aria-modal="true" (click)="$event.stopPropagation()">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">
              {{ credentialKind() === 'password' ? i18n.t('backoffice.users.resetPassword') : i18n.t('backoffice.users.resetPin') }}
            </h2>
            <p class="bo-panel__note mono">{{ target.username }}</p>
          </div>
          <div class="bo-panel__body">
            <div class="bo-field">
              <label for="us-new">
                {{ i18n.t('backoffice.users.newValue') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
              </label>
              @if (credentialKind() === 'password') {
                <input id="us-new" [(ngModel)]="credentialValue" type="password" autocomplete="new-password" />
              } @else {
                <input id="us-new" [(ngModel)]="credentialValue" type="text" autocomplete="new-password" />
              }
            </div>
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-ghost" (click)="credentialTarget.set(null)">{{ i18n.t('common.cancel') }}</button>
            <button type="button" class="btn-primary" [disabled]="!credentialValue" (click)="submitCredential()">
              {{ i18n.t('common.save') }}
            </button>
          </div>
        </div>
      </div>
    }

    <!-- The account is ready: its Login ID, drawn by the server, shown once large enough to read out
         and copy. It is in the list from now on, so nothing is lost by closing this. -->
    @if (created(); as c) {
      <div class="bo-backdrop" (click)="created.set(null)">
        <div
          class="bo-modal bo-modal--narrow"
          role="dialog"
          aria-modal="true"
          aria-labelledby="us-created-title"
          (click)="$event.stopPropagation()"
          (keydown.escape)="created.set(null)"
        >
          <div class="bo-panel__head">
            <h2 id="us-created-title" class="bo-panel__title">{{ i18n.t('backoffice.users.created.title') }}</h2>
            <p class="bo-panel__note">{{ c.name }}</p>
          </div>
          <div class="bo-panel__body">
            <p class="bo-fixed-label">{{ i18n.t('backoffice.users.loginId') }}</p>
            <p class="us-login-id mono num" data-login-id>{{ c.loginId }}</p>
            <p class="bo-hint">{{ i18n.t('backoffice.users.created.hint') }}</p>
          </div>
          <div class="bo-panel__foot">
            <button type="button" class="btn-ghost" (click)="copy(c.loginId)">
              {{ i18n.t(copied() ? 'backoffice.users.copied' : 'backoffice.users.copy') }}
            </button>
            <button type="button" class="btn-primary" (click)="created.set(null)">{{ i18n.t('backoffice.users.created.done') }}</button>
          </div>
        </div>
      </div>
    }
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      .us-login-id {
        margin: var(--sp-2) 0 var(--sp-3);
        font-size: var(--fs-xl);
        font-weight: 700;
        letter-spacing: 0.12em;
        color: var(--text);
        /* Digits read left to right in Arabic too, by themselves; the line starts at the reader's side. */
        unicode-bidi: isolate;
        text-align: start;
      }
      .us-till {
        white-space: nowrap;
      }
      .us-unlinked {
        display: block;
        margin-block-start: var(--sp-1);
      }
      .us-count {
        white-space: nowrap;
      }
      .us-perms-body {
        display: grid;
        gap: var(--sp-3);
      }
      .us-summary {
        margin: 0;
        color: var(--text-2);
      }
      /* A module inside the section: a quieter card than the section around it. */
      .us-module {
        box-shadow: none;
      }
      .us-keys {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(min(100%, 18rem), 1fr));
        gap: var(--sp-2) var(--sp-4);
      }
      /* A long name wraps on a phone: the box grows with it rather than letting it spill onto the next. */
      .us-key .bo-check {
        block-size: auto;
      }
      .us-key .bo-hint {
        margin-block-start: var(--sp-1);
        padding-inline-start: var(--sp-5);
      }
      /* What the account holds, in words: kept to two lines so one long list cannot stretch the row. */
      .us-perms {
        max-inline-size: 18rem;
      }
      .us-perms__text {
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
        font-size: var(--fs-meta);
        color: var(--text-2);
      }
    `,
  ],
})
export class BackofficeUsersComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly pages = inject(AdminPagesApi);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  private readonly hr = inject(HrApi);
  private readonly route = inject(ActivatedRoute, { optional: true });
  readonly i18n = inject(I18nService);
  /** An employee number in an option's text, kept one left-to-right run beside Arabic words (core/bidi.ts). */
  readonly isolate = isolate;

  /** The page on screen; the server filters and pages the accounts (owner, 2026-11-17: "paginate in backend"). */
  readonly users = computed(() => this.pager.rows());
  /**
   * The company's roles (GET /api/roles). Until they load — or from a server without them — the three system
   * roles stand in, at their fixed ids, holding what the screen has always shown them holding.
   */
  readonly roles = signal<AdminRoleRow[]>(LEGACY_ROLES);

  activeTone(isActive: boolean): StatusTone {
    return toneOf('active', isActive ? 'Active' : 'Inactive');
  }
  /** Every branch, as the pickers read them. */
  private readonly allBranches = signal<AdminBranch[]>([]);
  /** The branches offered: the active ones, and any an account still works at (the server says which, over all accounts). */
  readonly branches = computed(() => {
    const held = new Set(this.pager.summary()?.branches ?? []);
    return this.allBranches().filter((b) => b.isActive || held.has(b.id));
  });
  /** Everyone still working, as Staff lists them; the form offers those without an active account. */
  readonly employees = signal<Employee[]>([]);
  /** The employees an account can be given to now: still working, with no active account. */
  readonly available = computed(() => this.employees().filter((e) => !e.accountUserId));

  // The list's filters (owner, 2026-11-17), applied on the server over every account.
  /** A company role by its id, a system role (or none) by its word — the roles the column names. */
  readonly roleFilter = signal<string>('all');
  readonly branchFilter = signal<string>('all');
  readonly stateFilter = signal<'all' | 'active' | 'inactive'>('all');
  readonly linkFilter = signal<'all' | 'linked' | 'unlinked'>('all');
  /** The roles the accounts are on, over all of them (the server's summary), in the words the column uses. */
  readonly roleOptions = computed(() =>
    (this.pager.summary()?.roles ?? [])
      .map((o) => ({ value: o.value, label: o.name ?? this.roleLabel(o.value as AdminRole) }))
      .sort((a, b) => a.label.localeCompare(b.label)),
  );
  readonly filtered = computed(
    () =>
      this.roleFilter() !== 'all' ||
      this.branchFilter() !== 'all' ||
      this.stateFilter() !== 'all' ||
      this.linkFilter() !== 'all',
  );
  /** The accounts a page at a time from the server, by username; a filter change goes back to page one. */
  readonly pager = createServerPager(
    (page) => this.pages.usersPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'admin.users',
      filters: () => ({
        role: this.roleFilter(),
        branch: this.branchFilter(),
        state: this.stateFilter(),
        link: this.linkFilter(),
      }),
    },
  );

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  clearFilters(): void {
    this.roleFilter.set('all');
    this.branchFilter.set('all');
    this.stateFilter.set('all');
    this.linkFilter.set('all');
  }
  /** The account just created and the Login ID the server drew for it. */
  readonly created = signal<{ name: string; loginId: string } | null>(null);
  readonly copied = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly editingId = signal<string | null>(null);
  readonly editorOpen = signal(false);
  readonly credentialTarget = signal<AdminUser | null>(null);
  readonly credentialKind = signal<CredentialKind>('password');

  /** Which required fields have earned the right to complain. */
  readonly touched = new TouchedFields();
  /** The server's floor, stated in the field rather than discovered on refusal. */
  readonly minPassword = MIN_PASSWORD_LENGTH;

  /** The permission catalogue by module (GET /api/permissions), in the order the server lists it. */
  readonly catalogue = signal<PermissionModule[]>([]);
  /** The keys given to this account on top of its role — never one the role gives: those are the role's. */
  readonly extras = signal<ReadonlySet<string>>(new Set());
  /** The account's authorization version as it was opened, sent back so a save on an older view is refused. */
  private openedVersion: number | undefined;

  /** The employee a new account is for — required (2026-10-05). */
  employeeId: string | null = null;
  /** On an edit: what the account signs in with, and whether it already belongs to an employee. */
  editingLoginId: string | null = null;
  editingEmployeeId: string | null = null;
  /** On an edit of an unlinked account: the employee to link it to. */
  linkEmployeeId: string | null = null;
  username = '';
  fullName = '';
  role: AdminRole = 'Cashier';
  /** The role chosen, by id; `role` above is its scope read as Admin, Manager or Cashier, which the form's rules use. */
  roleId: string = CASHIER_ROLE_ID;
  /** The branch this user works at — required for every role (BranchLock); null only until chosen. */
  branchId: string | null = null;
  password = '';
  pin = '';
  isActive = true;
  credentialValue = '';

  async ngOnInit(): Promise<void> {
    // The list loads itself through its pager; this reads what the form and the filters offer.
    await this.loadContext();
    // Opened from an employee's record ("give access"): the form starts on that employee.
    const asked = this.route?.snapshot.queryParamMap.get('employee');
    if (asked && this.available().some((e) => e.id === asked)) {
      this.openCreate();
      this.employeeId = asked;
      this.employeeChosen(asked);
    }
  }

  /** The account works where its employee does, unless that is a warehouse and the role sells. */
  employeeChosen(id: string | null): void {
    const employee = this.employees().find((e) => e.id === id);
    if (!employee) return;
    const branch = this.branches().find((b) => b.id === employee.branchId);
    this.branchId = branch && !(this.role === 'Cashier' && branch.isWarehouse) ? branch.id : this.branchId;
  }

  /** Required for a new account; said once the field was left, or a save was tried, without one. */
  employeeMissing(): boolean {
    return !this.editingId() && this.touched.missing('employeeId', this.employeeId ?? '');
  }

  async copy(value: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(value);
      this.copied.set(true);
    } catch {
      // No clipboard (an insecure origin, a refused permission): the number is on screen to read out.
    }
  }

  /** Links the account being edited to its employee; it keeps signing in as it did. */
  async link(): Promise<void> {
    const id = this.editingId();
    if (!id || !this.linkEmployeeId) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('backoffice.users.link.action'))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const linked = await this.admin.linkUserEmployee(id, this.linkEmployeeId);
      this.editingEmployeeId = linked.employeeId ?? null;
      this.fullName = linked.fullName;
      this.linkEmployeeId = null;
      await this.reload();
    } catch (e) {
      this.error.set(this.problemMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  /** A refusal in the reader's words when the server gave it a code this screen knows. */
  private problemMessage(error: unknown): string {
    const code = problemCode(error);
    if (code === CASHIER_ON_WAREHOUSE) return this.i18n.t('backoffice.users.cashierOnWarehouse');
    if (code === USER_CHANGED) return this.i18n.t('users.perm.changed');
    if (code && PERMISSION_PROBLEMS.includes(code)) return this.i18n.t('roles.error.' + code);
    if (code && ACCOUNT_PROBLEMS.includes(code)) return this.i18n.t('backoffice.users.problem.' + code);
    return adminErrorMessage(error);
  }

  private resetForm(): void {
    this.employeeId = null;
    this.editingLoginId = null;
    this.editingEmployeeId = null;
    this.linkEmployeeId = null;
    this.username = '';
    this.fullName = '';
    this.chooseRole(CASHIER_ROLE_ID);
    this.branchId = null;
    this.extras.set(new Set());
    this.openedVersion = undefined;
    this.password = '';
    this.pin = '';
    this.isActive = true;
  }

  // ---------- permissions ----------

  /**
   * Given by the chosen role, so shown ticked and locked: the role gives it whatever is ticked here. A role
   * switched off gives nothing, as the server reads it (ServerAuthorization.Combine).
   */
  byRole(key: string): boolean {
    const role = this.chosenRole();
    return !!role?.isActive && role.permissions.includes(key);
  }

  /** Held by the account: by its role or given to it. */
  held(key: string): boolean {
    return this.byRole(key) || this.extras().has(key);
  }

  /** A key the reader holds, or covers — never more than their own authority (UserGrantEditor on the server). */
  mayChange(key: string): boolean {
    return mayGive(this.auth.currentUser(), key);
  }

  toggle(key: string, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    const next = new Set(this.extras());
    if (on) next.add(key);
    else next.delete(key);
    this.extras.set(next);
  }

  /** The explanation the screen has always given beneath a key, when it has one. */
  hintOf(key: string): string | null {
    return KEY_HINTS[key] ?? null;
  }

  heldIn(module: PermissionModule): number {
    return module.keys.filter((k) => this.held(k)).length;
  }

  ownIn(module: PermissionModule): number {
    return module.keys.filter((k) => !this.byRole(k) && this.extras().has(k)).length;
  }

  catalogueSize(): number {
    return this.catalogue().reduce((n, m) => n + m.keys.length, 0);
  }

  effectiveCount(): number {
    return this.catalogue().reduce((n, m) => n + this.heldIn(m), 0);
  }

  /**
   * What the save sends as the account's own: every key given to it that the role does not give, in the
   * catalogue's order. Read from what was given, not from the catalogue, so a catalogue that failed to load
   * never takes anything away.
   */
  ownKeys(): string[] {
    const order = this.catalogue().flatMap((m) => m.keys);
    const position = (k: string) => (order.includes(k) ? order.indexOf(k) : order.length);
    return [...this.extras()].filter((k) => !this.byRole(k)).sort((a, b) => position(a) - position(b));
  }

  /** The effective permissions in one sentence: from the role, given to this person, in all. */
  summary(): string {
    const role = this.chosenRole();
    const fromRole = this.catalogue().reduce((n, m) => n + m.keys.filter((k) => this.byRole(k)).length, 0);
    return this.i18n.t('users.perm.summary', {
      role: role ? isolate(this.roleRowLabel(role)) : '—',
      fromRole,
      own: this.ownKeys().length,
      held: this.effectiveCount(),
      total: this.catalogueSize(),
    });
  }

  namesOf(keys: readonly string[]): string {
    return keys.map((k) => this.i18n.t('perm.' + k)).join(' · ');
  }

  /** The roles the field offers: every active one, and the one the account is on if it was switched off. */
  offeredRoles(): AdminRoleRow[] {
    return this.roles().filter((r) => r.isActive || r.id === this.roleId);
  }

  private chosenRole(): AdminRoleRow | undefined {
    return this.roles().find((r) => r.id === this.roleId);
  }

  /** Chooses a role; the form's rules (a till role sells, so not on a warehouse) read its scope. */
  chooseRole(id: string): void {
    this.roleId = id;
    const scope = this.roles().find((r) => r.id === id)?.scope;
    this.role = scope === 'Company' ? 'Admin' : scope === 'AssignedBranch' ? 'Manager' : 'Cashier';
  }

  /** A system role in the reader's language; a role the company made, by its name. */
  roleRowLabel(role: AdminRoleRow): string {
    return role.key ? this.i18n.t(`backoffice.users.role.${role.key}`) : role.name;
  }

  /** The role a row's account is on: by name for a role the company made, in words for a system one. */
  userRoleLabel(u: AdminUser): string {
    const role = u.roleId ? this.roles().find((r) => r.id === u.roleId) : undefined;
    if (role && !role.key) return role.name;
    if (!role && u.roleName && u.roleId && !SYSTEM_ROLE_IDS.includes(u.roleId)) return u.roleName;
    return this.roleLabel(u.role);
  }

  /**
   * The row's permissions beside its role (§17.10): "the role's only", or how many are given on top and which. A
   * server from before reports only the legacy keys, in words, as the column always showed them.
   */
  permissionsLabel(u: AdminUser): string {
    const own = u.additionalPermissions;
    if (own) {
      return own.length
        ? this.i18n.t('users.perm.rowOwn', { n: own.length, list: this.namesOf(own) })
        : this.i18n.t('users.perm.roleOnly');
    }
    const held = u.effectivePermissions ?? u.permissions ?? [];
    return held.length ? this.namesOf(held) : this.i18n.t('backoffice.users.permissions.none');
  }

  /** A branch named in the reader's language, not always in French. */
  name(record: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(record, this.i18n.lang());
  }

  /**
   * The user's branch in words. A legacy row with none says so — "no branch assigned", working
   * at the main branch — so the owner sees whom to assign.
   */
  branchLabel(u: AdminUser): string {
    const branch = u.branchId ? this.branches().find((b) => b.id === u.branchId) : undefined;
    return branch ? this.name(branch) : this.i18n.t('backoffice.users.branch.none');
  }

  /**
   * The cashier's own till, as the server reports it: its code, with "switched off" in words when
   * it is; a dash for a user with none (the back office signs in without a till).
   */
  tillLabel(u: AdminUser): string {
    if (!u.tillCode) return '—';
    return u.tillActive ? u.tillCode : `${u.tillCode} — ${this.i18n.t('common.inactive')}`;
  }

  /** Required for every role; said once the field was left, or a save was tried, without one. */
  branchMissing(): boolean {
    return this.touched.missing('branchId', this.branchId ?? '');
  }

  /** What the server is told: the chosen branch, whatever the role. */
  private boundBranch(): string | null {
    return this.branchId;
  }

  canSave(): boolean {
    if (!this.branchId) return false;
    if (!this.editingId()) return !!this.employeeId && this.password.trim().length >= this.minPassword;
    // A linked account's name is its employee's; only an unlinked one has its own to fill in.
    return !!this.editingEmployeeId || !!this.fullName.trim();
  }

  /** A new account needs a password the server will accept; an edit never asks for one. */
  passwordShort(): boolean {
    if (this.editingId()) return false;
    return this.touched.missing('password', this.password) || (!!this.password && this.password.trim().length < this.minPassword);
  }

  roleLabel(role: AdminRole): string {
    return this.i18n.t(`backoffice.users.role.${role.toLowerCase()}`);
  }

  isSelf(u: AdminUser): boolean {
    return this.auth.currentUser()?.id === u.id;
  }

  editingSelf(): boolean {
    return this.editingId() === this.auth.currentUser()?.id;
  }

  readonly twoFactorTone = toneOf('twoFactor', 'on');

  /**
   * Two-step login is each user's own (owner, 2026-11-11): the administrator can only turn it off, for
   * someone who lost both the phone and the recovery codes. The account is signed out everywhere.
   */
  async resetTwoFactor(u: AdminUser): Promise<void> {
    const name = u.fullName || u.username;
    const reset = await this.confirm.ask({
      title: this.i18n.t('backoffice.users.twoFactorReset'),
      message: this.i18n.t('backoffice.users.twoFactorResetConfirm', { name }),
      confirmLabel: this.i18n.t('backoffice.users.twoFactorReset'),
      danger: true,
    });
    if (!reset) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.admin.resetTwoFactor(u.id);
      if (this.isSelf(u)) {
        await this.auth.logout();
        return;
      }
      // The row as the server now holds it, on the page on screen.
      await this.pager.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async revokeSessions(u: AdminUser): Promise<void> {
    // Not a delete — the user simply signs in again — so the ordinary, not the danger, style.
    const revoke = await this.confirm.ask({
      title: this.i18n.t('users.revoke.title', { name: u.fullName || u.username }),
      message: this.i18n.t('backoffice.users.revokeConfirm'),
      confirmLabel: this.i18n.t('backoffice.users.revokeSessions'),
    });
    if (!revoke) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.admin.revokeSessions(u.id);
      if (this.isSelf(u)) await this.auth.logout();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  /** After a change: what the form offers, and the page on screen, read again. */
  async reload(): Promise<void> {
    await Promise.all([this.loadContext(), this.pager.reload()]);
  }

  /** The branches, the staff, the roles and the permission catalogue the form and the filters offer. */
  private async loadContext(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const [branches, staff, roles, catalogue] = await Promise.all([
        this.admin.listBranches(),
        // Everyone still working: the people an account can be given to. A refusal here (no access to
        // Staff) leaves the list empty and the form says so, rather than breaking the whole screen.
        this.hr.employees({ active: true, take: 1000 }).then((page) => page.rows).catch(() => [] as Employee[]),
        // The roles: a refusal leaves the three system roles, which is what the field offered before.
        (async () => this.admin.listRoles())().catch(() => LEGACY_ROLES),
        // The catalogue: a refusal leaves the section saying so, and a save keeps the account's own keys as they were.
        (async () => this.admin.listPermissions())().catch(() => [] as PermissionModule[]),
      ]);
      this.roles.set(roles.length ? roles : LEGACY_ROLES);
      this.catalogue.set(catalogue);
      this.allBranches.set(branches);
      this.employees.set(staff);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  openCreate(): void {
    this.cancelEdit();
    this.editorOpen.set(true);
  }

  edit(u: AdminUser): void {
    this.editingId.set(u.id);
    this.username = u.username;
    this.editingLoginId = u.loginId ?? null;
    this.editingEmployeeId = u.employeeId ?? null;
    this.fullName = u.fullName;
    this.chooseRole(u.roleId ?? ROLE_ID_OF[u.role]);
    this.branchId = u.branchId ?? null;
    // The account's own keys, never the effective ones: what the role gives stays the role's.
    this.extras.set(new Set(u.additionalPermissions ?? []));
    this.openedVersion = u.authorizationVersion;
    this.isActive = u.isActive;
    this.editorOpen.set(true);
  }

  cancelEdit(): void {
    this.editorOpen.set(false);
    this.editingId.set(null);
    this.touched.reset();
    this.resetForm();
  }

  async save(): Promise<void> {
    this.touched.touchAll('employeeId', 'fullName', 'password', 'branchId');
    if (!this.canSave()) return;
    const name = this.fullName || this.employees().find(e => e.id === this.employeeId)?.fullName;
    if (!(await this.confirm.ask(saveRequest(this.i18n, name)))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const id = this.editingId();
      if (id) {
        const payload: UpdateUserRequest = {
          fullName: this.fullName.trim(),
          role: this.role,
          roleId: this.roleId,
          isActive: this.isActive,
          additionalPermissions: this.ownKeys(),
          ...(this.openedVersion === undefined ? {} : { authorizationVersion: this.openedVersion }),
          branchId: this.boundBranch(),
        };
        await this.admin.updateUser(id, payload);
        this.cancelEdit();
      } else {
        const payload: CreateUserRequest = {
          employeeId: this.employeeId!,
          role: this.role,
          roleId: this.roleId,
          password: this.password,
          pin: this.pin.trim() || null,
          additionalPermissions: this.ownKeys(),
          branchId: this.boundBranch(),
        };
        const account = await this.admin.createUser(payload);
        this.cancelEdit();
        this.copied.set(false);
        this.created.set({ name: account.fullName, loginId: account.loginId ?? account.username });
      }
      await this.reload();
    } catch (e) {
      // A refusal the server named (a cashier on a warehouse, an employee who already has an account…)
      // is said in words, not as the server's raw body.
      this.error.set(this.problemMessage(e));
      // Changed by someone else meanwhile: the form shows an older account, so it closes and the list is read
      // again; opened anew, it shows what is there now.
      if (problemCode(e) === USER_CHANGED) {
        this.cancelEdit();
        await this.reload();
        this.error.set(this.i18n.t('users.perm.changed'));
      }
    } finally {
      this.busy.set(false);
    }
  }

  openCredential(u: AdminUser, kind: CredentialKind): void {
    this.credentialTarget.set(u);
    this.credentialKind.set(kind);
    this.credentialValue = '';
  }

  async submitCredential(): Promise<void> {
    const target = this.credentialTarget();
    if (!target || !this.credentialValue) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('common.save'))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      if (this.credentialKind() === 'password') await this.admin.resetPassword(target.id, this.credentialValue);
      else await this.admin.resetPin(target.id, this.credentialValue);
      this.credentialTarget.set(null);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async deactivate(u: AdminUser): Promise<void> {
    if (this.isSelf(u)) return;
    const title = this.i18n.t('users.deactivate.title', { name: u.fullName || u.username });
    const yes = await this.confirm.ask({
      ...deleteRequest(this.i18n, title, this.i18n.t('users.deactivate.message')),
      confirmLabel: this.i18n.t('backoffice.users.deactivate'),
    });
    if (!yes) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.admin.deactivateUser(u.id);
      await this.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
