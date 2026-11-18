import { TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { PageResult } from '../../../core/paging';
import { MaintenanceJob, MaintenanceJobSummary, MaintenanceJobWork, MaintenanceWaiting } from './maintenance-api.service';

/** Shared fixtures for the maintenance specs (Phase M1). */
export function summary(over: Partial<MaintenanceJobSummary> = {}): MaintenanceJobSummary {
  return {
    id: 'j1', number: 'MJ-000001', branchId: 'b1', branchName: 'Alger-Centre', kind: 'Recharged', status: 'Open',
    productId: 'p1', productSku: 'ECH-01', productNameAr: 'سقالة', productNameFr: 'Échafaudage', productNameEn: 'Scaffold',
    qty: 8, repairedQty: 0, scrappedQty: 0, itemReference: null, contractId: 'c1', contractNumber: 'CTR-0001',
    returnNoteId: 'n1', returnNoteNumber: 'RET-000001', createdAtUtc: '2026-11-09T08:00:00Z', completedAtUtc: null,
    ...over,
  };
}

export function job(over: Partial<MaintenanceJob> = {}): MaintenanceJob {
  return {
    ...summary(),
    returnNoteLineId: 'l1', customerName: 'Ben Ali BTP', lineDamagedQty: 8, lineRecharge: 16000,
    description: 'Cadres tordus', diagnosis: null, createdBy: 'chef', openedByUserId: 'u-chef', openedBy: 'chef',
    openedAtUtc: '2026-11-09T08:00:00Z', completedBy: null, cancelledBy: null, cancelledAtUtc: null, cancelReason: null,
    inMaintenanceAtBranch: 8, jobCloser: 'AnyHolder',
    events: [{ kind: 'Opened', detail: 'Recharged 8', userName: 'chef', atUtc: '2026-11-09T08:00:00Z' }],
    ...over,
  };
}

/** A job's parts and outside work (Phase M2): nothing by default. */
export function work(over: Partial<MaintenanceJobWork> = {}): MaintenanceJobWork {
  return {
    jobId: 'j1', jobStatus: 'Open', parts: [], movements: [], expenses: [], partsValue: 0, partsAtJobQty: 0, expensesTotal: 0,
    partsPosting: 'PracticeLedgerByMonth', ...over,
  };
}

/** Rods: 22 issued at 40 on MJI-000001, 5 back on MJR-000001, so 17 at the job worth 680; paint all used up there. */
export function workWithParts(over: Partial<MaintenanceJobWork> = {}): MaintenanceJobWork {
  return work({
    parts: [
      { productId: 'rod', sku: 'ROD-25', nameAr: 'قضيب', nameFr: 'Baguette', nameEn: 'Rod', issuedQty: 22, returnedQty: 5, atJobQty: 17, issueUnitCost: 40, value: 680 },
      { productId: 'pnt', sku: 'PNT-01', nameAr: 'طلاء', nameFr: 'Peinture', nameEn: 'Paint', issuedQty: 1, returnedQty: 1, atJobQty: 0, issueUnitCost: 900, value: 0 },
    ],
    movements: [
      { documentId: 'd1', documentNumber: 'MJI-000001', kind: 'Issue', productId: 'rod', sku: 'ROD-25', qty: 22, unitCost: 40, value: 880,
        costBasis: 'CostPrice', note: null, userName: 'chef', atUtc: '2026-11-09T09:00:00Z' },
      { documentId: 'd1', documentNumber: 'MJI-000001', kind: 'Issue', productId: 'pnt', sku: 'PNT-01', qty: 1, unitCost: 900, value: 900,
        costBasis: 'CostPrice', note: null, userName: 'chef', atUtc: '2026-11-09T09:00:00Z' },
      { documentId: 'd2', documentNumber: 'MJR-000001', kind: 'Return', productId: 'rod', sku: 'ROD-25', qty: 5, unitCost: 40, value: 200,
        costBasis: 'CostPrice', note: null, userName: 'chef', atUtc: '2026-11-09T10:00:00Z' },
      { documentId: 'd3', documentNumber: 'MJR-000002', kind: 'Return', productId: 'pnt', sku: 'PNT-01', qty: 1, unitCost: 900, value: 900,
        costBasis: 'CostPrice', note: null, userName: 'chef', atUtc: '2026-11-09T10:00:00Z' },
    ],
    expenses: [
      { id: 'e1', description: 'Soudeur', amount: 8000, paymentVoucherId: 'v1', voucherNumber: 'PV-000801', voucherStatus: 'Paid',
        userName: 'chef', atUtc: '2026-11-09T11:00:00Z' },
    ],
    partsValue: 680, partsAtJobQty: 17, expensesTotal: 8000,
    ...over,
  });
}

export const WAITING: MaintenanceWaiting = {
  buckets: [
    { branchId: 'b1', branchName: 'Alger-Centre', productId: 'p1', sku: 'ECH-01', nameAr: 'سقالة', nameFr: 'Échafaudage', nameEn: 'Scaffold',
      inMaintenance: 20, heldByJobs: 12, free: 8 },
    { branchId: 'b1', branchName: 'Alger-Centre', productId: 'p2', sku: 'COL-01', nameAr: 'مشبك', nameFr: 'Collier', nameEn: 'Coupler',
      inMaintenance: 5, heldByJobs: 5, free: 0 },
  ],
  lines: [
    { returnNoteLineId: 'l1', returnNoteId: 'n1', returnNoteNumber: 'RET-000001', contractId: 'c1', contractNumber: 'CTR-0001',
      customerName: 'Ben Ali BTP', branchId: 'b1', branchName: 'Alger-Centre', productId: 'p1', sku: 'ECH-01', nameAr: 'سقالة',
      nameFr: 'Échafaudage', nameEn: 'Scaffold', returnedAtUtc: '2026-11-08T09:00:00Z', damagedQty: 20, coveredQty: 12, openQty: 8, recharge: 16000 },
  ],
};

/** One page as the server answers it (owner, 2026-11-17: server paging): these rows, their count, and the list's summary. */
export function pageOf<T, S = unknown>(items: T[], summary: S | null = null, take = 10): PageResult<T, S> {
  return { items, total: items.length, skip: 0, take, summary };
}

/** Lets a filter's 300 ms wait pass and the page it asks for arrive. */
export async function afterFilter(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }): Promise<void> {
  fixture.detectChanges();
  await new Promise((resolve) => setTimeout(resolve, 350));
  await fixture.whenStable();
  fixture.detectChanges();
}

/** The translations, and a signed-in reader holding exactly these keys. */
export function signIn(keys: string[], id = 'u-reader'): void {
  TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  TestBed.inject(AuthService).currentUser.set({
    id, username: 'u', displayName: 'User', role: 'Manager', permissions: [], capabilities: [],
    permissionKeys: keys, scope: 'AssignedBranch',
  } as never);
}

export const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params as never);
