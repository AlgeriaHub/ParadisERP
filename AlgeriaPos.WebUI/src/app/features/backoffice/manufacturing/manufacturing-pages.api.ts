import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { PageResult, pageQueryString } from '../../../core/paging';
import { PracticeEntry, PracticeLedger } from './costing-settings-api.service';
import {
  BomSummary,
  JobRoleDto,
  LineStockRow,
  MaterialDto,
  ProductionLineSummary,
  StageTemplateDto,
  UnitDto,
} from './manufacturing-api.service';
import {
  LabourAbsorptionRow,
  LabourLineMonth,
  MaterialFlowIssue,
  PendingMovementRow,
  PosExceptionRow,
  ProductionLogSummary,
  ProductionOrderSummary,
  WorkInProgressLine,
  WorkInProgressRow,
} from './production-api.service';

/**
 * The manufacturing lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend";
 * `AlgeriaPos.API/Endpoints/Paging/ManufacturingPageRoutes.cs`). Each call asks one page of a list the server
 * filters over every row; a filter left at "all", empty or null is not sent. The whole-list calls in
 * `manufacturing-api.service.ts` and `production-api.service.ts` stay for the pickers and the forms.
 */

type Filters = Record<string, string | number | boolean | null | undefined>;

/** The page and its size, as the server pager asks them. */
export interface PageAsk {
  skip: number;
  take: number;
}

/** The lines' tiles: every line the state choice keeps. */
export interface LinesPageSummary {
  lines: number;
  employees: number;
  activeBoms: number;
}

/** A list's whole count and the values a filter offers. */
export interface ChoicesPageSummary {
  all: number;
  choices: string[];
}

export interface PosExceptionsPageSummary {
  sales: number;
  totalQuantity: number;
  staleCount: number;
  afterWithdrawalCount: number;
  unknownCount: number;
  roles: string[];
}

export interface PendingMovementsPageSummary {
  issuedValue: number;
  returnedValue: number;
  releasedValue: number | null;
  workInProgressValue: number;
  releasesValued: boolean;
  all: number;
  kinds: string[];
}

export interface MaterialFlowPageSummary {
  issues: number;
  movementsChecked: number;
  materialsChecked: number;
  stocksChecked: number;
  kinds: string[];
}

export interface WorkInProgressPageSummary {
  costToDate: number | null;
  releasedValue: number | null;
  atLineValue: number;
  workInProgress: number | null;
  lines?: WorkInProgressLine[];
  moneyShown: boolean;
  all: number;
  statuses: string[];
}

export interface LabourAbsorptionPageSummary {
  year: number;
  month: number;
  moneyShown: boolean;
  orderHours: number;
  otherHours: number;
  employerCost: number | null;
  absorbedCost: number | null;
  unabsorbedCost: number | null;
  employerSocialMissing: boolean;
  lines?: LabourLineMonth[];
  labourAbsorption?: 'BlendedActualRate' | 'PremiumToOvertime';
  premiumInCharges?: number | null;
  all: number;
  bases: string[];
  notApproved: boolean;
}

export interface LedgerBranch {
  id: string;
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
}

/**
 * The practice ledger's month without its entries and late costs (`PracticeLedger` less those two lists), with
 * how many of each it holds and the kinds and branches the entries name.
 */
export type PracticeLedgerPageSummary = Omit<PracticeLedger, 'entries' | 'lateCosts'> & {
  entryCount: number;
  lateCostCount: number;
  kinds: string[];
  branches: LedgerBranch[];
};

export type PracticeLateCost = PracticeLedger['lateCosts'][number];

const BASE = '/api/manufacturing';

@Injectable({ providedIn: 'root' })
export class ManufacturingPagesApi {
  private readonly api = inject(ApiService);

  private page<T, S = unknown>(path: string, ask: PageAsk, filters: Filters): Promise<PageResult<T, S>> {
    return this.api.getJson<PageResult<T, S>>(`${BASE}${path}${pageQueryString({ ...filters, skip: ask.skip, take: ask.take })}`);
  }

  // ---------- configuration ----------
  units(ask: PageAsk, f: { dimension?: string; state?: string }): Promise<PageResult<UnitDto>> {
    return this.page('/units/page', ask, f);
  }
  materials(ask: PageAsk, f: { q?: string; role?: string; till?: string; baseUnit?: string }): Promise<PageResult<MaterialDto>> {
    return this.page('/materials/page', ask, f);
  }
  jobRoles(ask: PageAsk, f: { state?: string }): Promise<PageResult<JobRoleDto>> {
    return this.page('/job-roles/page', ask, f);
  }
  stageTemplates(ask: PageAsk, f: { product?: string; state?: string }): Promise<PageResult<StageTemplateDto>> {
    return this.page('/stage-templates/page', ask, f);
  }
  lines(ask: PageAsk, f: { includeInactive?: boolean; manager?: string; boms?: string }): Promise<PageResult<ProductionLineSummary, LinesPageSummary>> {
    // `includeInactive=false` is the list route's default: sent only when true.
    return this.page('/lines/page', ask, { ...f, includeInactive: f.includeInactive || null });
  }
  lineStock(lineId: string, ask: PageAsk, f: { role?: string }): Promise<PageResult<LineStockRow, ChoicesPageSummary>> {
    return this.page(`/lines/${lineId}/stock/page`, ask, f);
  }
  boms(ask: PageAsk, f: { lineId?: string | null; status?: string | null; q?: string; scope?: string }): Promise<PageResult<BomSummary>> {
    return this.page('/boms/page', ask, f);
  }

  // ---------- operations ----------
  orders(
    ask: PageAsk,
    f: { lineId?: string | null; status?: string | null; q?: string; late?: string; startedShort?: string; endFrom?: string; endTo?: string },
  ): Promise<PageResult<ProductionOrderSummary>> {
    return this.page('/orders/page', ask, f);
  }
  logs(ask: PageAsk, f: { lineId?: string | null; status?: string | null; from?: string; to?: string }): Promise<PageResult<ProductionLogSummary>> {
    return this.page('/logs/page', ask, f);
  }

  // ---------- reports ----------
  posExceptions(
    ask: PageAsk,
    f: { from?: string | null; to?: string | null; branchId?: string | null; productId?: string | null; sale?: string | null; stale?: string | null; role?: string },
  ): Promise<PageResult<PosExceptionRow, PosExceptionsPageSummary>> {
    return this.page('/reports/pos-exceptions/page', ask, f);
  }
  pendingMovements(
    ask: PageAsk,
    f: { lineId?: string | null; kind?: string; from?: string; to?: string },
  ): Promise<PageResult<PendingMovementRow, PendingMovementsPageSummary>> {
    return this.page('/reports/pending-movements/page', ask, f);
  }
  materialFlow(ask: PageAsk, f: { kind?: string }): Promise<PageResult<MaterialFlowIssue, MaterialFlowPageSummary>> {
    return this.page('/reports/material-flow/page', ask, f);
  }
  workInProgress(
    ask: PageAsk,
    f: { lineId?: string | null; status?: string; provisional?: string },
  ): Promise<PageResult<WorkInProgressRow, WorkInProgressPageSummary>> {
    return this.page('/reports/work-in-progress/page', ask, f);
  }
  labourAbsorption(
    ask: PageAsk,
    f: { year?: number | null; month?: number | null; lineId?: string | null; basis?: string },
  ): Promise<PageResult<LabourAbsorptionRow, LabourAbsorptionPageSummary>> {
    return this.page('/reports/labour-absorption/page', ask, f);
  }

  // ---------- the practice ledger ----------
  ledgerEntries(
    ask: PageAsk,
    f: { year?: number | null; month?: number | null; lineId?: string | null; kind?: string; entryBranchId?: string },
  ): Promise<PageResult<PracticeEntry, PracticeLedgerPageSummary>> {
    return this.page('/accounting/practice-ledger/entries/page', ask, f);
  }
  ledgerLateCosts(
    ask: PageAsk,
    f: { year?: number | null; month?: number | null; lineId?: string | null; element?: string },
  ): Promise<PageResult<PracticeLateCost, ChoicesPageSummary>> {
    return this.page('/accounting/practice-ledger/late-costs/page', ask, f);
  }
}
