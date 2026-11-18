import { Injectable, inject } from '@angular/core';
import type { AdminBranch, AdminDevice, AdminRoleRow, AdminUser } from '../../core/admin.service';
import { ApiService } from '../../core/api.service';
import { PageResult, pageQueryString } from '../../core/paging';

/**
 * The administration lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend"): users,
 * roles, branches and tills. Each call is `<the list's own route>/page` (AlgeriaPos.API/Endpoints/Paging/PeoplePageRoutes.cs):
 * the filters run on the server over every row and only the page comes back. A filter left at "all", empty or null is
 * not sent. The whole lists stay where pickers read them (`AdminService.listBranches`, `listRoles`).
 */

export interface UsersPageQuery {
  skip: number;
  take: number;
  /** A company role's id, or the role word (Admin, Manager, Cashier) for a system role. */
  role?: string;
  /** A branch id, or `none` for an account with no branch. */
  branch?: string;
  state?: 'all' | 'active' | 'inactive';
  link?: 'all' | 'linked' | 'unlinked';
  id?: string;
}

/** A role the role filter offers: a company role's id and name, or a role word and no name. */
export interface UserRoleOption {
  value: string;
  name: string | null;
}

/** Over every account, not the page: the roles they are on and the branches they work at. */
export interface UsersPageSummary {
  roles: UserRoleOption[];
  branches: string[];
}

export interface RolesPageQuery {
  skip: number;
  take: number;
  scope?: string;
  state?: 'all' | 'active' | 'inactive';
  kind?: 'all' | 'system' | 'custom';
  id?: string;
}

export interface BranchesPageQuery {
  skip: number;
  take: number;
  kind?: 'all' | 'branch' | 'warehouse';
  state?: 'all' | 'active' | 'inactive' | 'setup';
  id?: string;
}

/** The main branch and the main warehouse among the branches the reader sees, whatever the page. */
export interface BranchesPageSummary {
  mains: AdminBranch[];
}

export interface DevicesPageQuery {
  skip: number;
  take: number;
  branchId?: string | null;
  state?: 'all' | 'active' | 'inactive';
  sync?: 'all' | 'synced' | 'never';
}

@Injectable({ providedIn: 'root' })
export class AdminPagesApi {
  private readonly api = inject(ApiService);

  usersPage(query: UsersPageQuery): Promise<PageResult<AdminUser, UsersPageSummary>> {
    return this.api.getJson<PageResult<AdminUser, UsersPageSummary>>(`/api/users/page${pageQueryString({ ...query })}`);
  }

  rolesPage(query: RolesPageQuery): Promise<PageResult<AdminRoleRow>> {
    return this.api.getJson<PageResult<AdminRoleRow>>(`/api/roles/page${pageQueryString({ ...query })}`);
  }

  branchesPage(query: BranchesPageQuery): Promise<PageResult<AdminBranch, BranchesPageSummary>> {
    return this.api.getJson<PageResult<AdminBranch, BranchesPageSummary>>(
      `/api/catalog/branches/page${pageQueryString({ ...query })}`,
    );
  }

  devicesPage(query: DevicesPageQuery): Promise<PageResult<AdminDevice>> {
    return this.api.getJson<PageResult<AdminDevice>>(`/api/devices/page${pageQueryString({ ...query })}`);
  }
}
