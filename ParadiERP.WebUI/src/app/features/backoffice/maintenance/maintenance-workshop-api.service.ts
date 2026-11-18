import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/**
 * MS-04's workshop list (`/api/maintenance/workshop`, docs/maintenance/plan.md "MS-04 as built"): who of a branch may be
 * booked on its jobs while MS-04 says "only the workshop list". The reader's own branch; every branch for the Admin.
 */
export interface MaintenanceWorkshopMember {
  id: string;
  employeeId: string;
  number: string;
  fullName: string;
  /** The branch whose list it is. */
  branchId: string;
  branchName: string;
  /** Where the person works now: another branch when they have moved since. */
  employeeBranchId: string;
  employeeBranchName: string;
  employed: boolean;
  /** Still of the list's branch and employed today: MS-04's "only the workshop list" may book them there. */
  effective: boolean;
  addedBy: string | null;
  addedAtUtc: string;
}

export interface MaintenanceWorkshopEmployee {
  employeeId: string;
  number: string;
  fullName: string;
  branchId: string;
  branchName: string;
}

export interface MaintenanceWorkshopBranch {
  branchId: string;
  branchName: string;
  /** Employed there today. */
  employees: number;
  /** Of whom on its list. */
  onList: number;
}

export interface MaintenanceWorkshop {
  bookableEmployees: 'AnyEmployeeOfBranch' | 'WorkshopListOnly';
  members: MaintenanceWorkshopMember[];
  available: MaintenanceWorkshopEmployee[];
  branches: MaintenanceWorkshopBranch[];
}

/** A row's state in words: bookable, moved to another branch, or left. */
export type WorkshopMemberState = 'bookable' | 'moved' | 'left';

export function workshopMemberState(m: MaintenanceWorkshopMember): WorkshopMemberState {
  if (!m.employed) return 'left';
  return m.employeeBranchId === m.branchId ? 'bookable' : 'moved';
}

/** The branches whose list books nobody though they employ people: what "only the workshop list" would leave unbookable. */
export function emptyWorkshopBranches(w: MaintenanceWorkshop | null): MaintenanceWorkshopBranch[] {
  return (w?.branches ?? []).filter((b) => b.employees > 0 && b.onList === 0);
}

@Injectable({ providedIn: 'root' })
export class MaintenanceWorkshopApi {
  private readonly api = inject(ApiService);

  list(): Promise<MaintenanceWorkshop> {
    return this.api.getJson<MaintenanceWorkshop>('/api/maintenance/workshop');
  }

  /** Puts an employee on the list of their branch (the server keeps a non-admin to their own). */
  add(employeeId: string, branchId: string | null): Promise<MaintenanceWorkshop> {
    return this.api.postJson<MaintenanceWorkshop>('/api/maintenance/workshop', { employeeId, branchId });
  }

  remove(id: string): Promise<MaintenanceWorkshop> {
    return this.api.deleteJson<MaintenanceWorkshop>(`/api/maintenance/workshop/${encodeURIComponent(id)}`);
  }
}
