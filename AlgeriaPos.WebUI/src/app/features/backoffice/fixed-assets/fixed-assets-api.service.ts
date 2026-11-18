import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { PracticeAccount, PracticeEntry, PracticePosting } from '../manufacturing/costing-settings-api.service';

/** One slot of the register's account map (REN-12): the account it names, never guessed; the SCF heading as a hint. */
export interface FixedAssetSlot {
  slot: string;
  ledgerAccountId: string | null;
  accountCode: string | null;
  accountNameAr: string | null;
  accountNameFr: string | null;
  accountNameEn: string | null;
  hintCode: string | null;
  required: boolean;
  updatedAtUtc: string | null;
  updatedBy: string | null;
}

export type RentalEquipmentTreatment = 'Stock' | 'FixedAssets';
export type DepreciationMethod = 'StraightLine' | 'DecliningBalance';

/**
 * A hire type, its own useful life (null: the default) and its own depreciation method (each null: the company's).
 * `locked`: the register holds it since the switch-over (its life is fixed). `methodLocked`: depreciation is posted on it
 * (its method, coefficient and switch are fixed). `effective…`: what it is depreciated by; `suggestedCoefficient`: the tax
 * code's for its life (1.5 for 3–4 years, 2 for 5–6, 2.5 above 6).
 */
export interface FixedAssetLife {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  usefulLifeMonths: number | null;
  locked: boolean;
  method?: DepreciationMethod | null;
  decliningCoefficient?: number | null;
  switchToStraightLine?: boolean | null;
  suggestedCoefficient?: number | null;
  methodLocked?: boolean;
  effectiveMethod?: DepreciationMethod;
  effectiveCoefficient?: number | null;
  effectiveSwitchToStraightLine?: boolean;
}
export type FixedAssetOpeningValue = 'CostInForce' | 'AssetUnitValue';

export interface FixedAssetSettings {
  treatment: RentalEquipmentTreatment;
  method: DepreciationMethod;
  defaultUsefulLifeMonths: number | null;
  residualPercent: number;
  openingValue: FixedAssetOpeningValue;
  /** `yyyy-MM`. */
  switchMonth: string | null;
  switchedAtUtc: string | null;
  switchedBy: string | null;
  active: boolean;
  /** The switch-over is posted: the default life, residual, opening value and month are fixed. */
  depreciationLocked: boolean;
  lives: FixedAssetLife[];
  slots: FixedAssetSlot[];
  updatedAtUtc: string | null;
  updatedBy: string | null;
  /** Declining balance: switch to straight line when it is higher (on by default). */
  switchToStraightLine?: boolean;
  /** The company's method is fixed: a type that follows it has depreciation posted on it. */
  methodLocked?: boolean;
}

/** A type's own method: each field null follows the company's. */
export interface FixedAssetMethodLine {
  productId: string;
  method: DepreciationMethod | null;
  decliningCoefficient: number | null;
  switchToStraightLine: boolean | null;
}

export interface FixedAssetSettingsUpdate {
  treatment: RentalEquipmentTreatment;
  method: DepreciationMethod;
  defaultUsefulLifeMonths: number | null;
  residualPercent: number;
  openingValue: FixedAssetOpeningValue;
  switchMonth: string | null;
  lives: { productId: string; usefulLifeMonths: number | null }[];
  switchToStraightLine?: boolean;
  methods?: FixedAssetMethodLine[];
}

export interface FixedAssetSwitchLine {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  branchId: string;
  branchCode: string;
  branchNameAr: string;
  branchNameFr: string;
  branchNameEn: string;
  onHand: number;
  onRent: number;
  inMaintenance: number;
  inTransit: number;
  qty: number;
  unitCost: number;
  stockValue: number;
  openingValue: number;
  difference: number;
  usefulLifeMonths: number | null;
  scrapQty: number;
  scrapValue: number;
}

/** The switch-over, previewed or as posted, with what still stands in the way. */
export interface FixedAssetSwitch {
  switchMonth: string | null;
  costMethod: 'CostPrice' | 'WeightedAverage';
  openingValue: FixedAssetOpeningValue;
  posted: boolean;
  switchedAtUtc: string | null;
  switchedBy: string | null;
  lines: FixedAssetSwitchLine[];
  stockValue: number;
  openingTotal: number;
  difference: number;
  blocking: { code: string; message: string }[];
}

export interface FixedAssetPool {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  branchId: string;
  branchCode: string;
  branchNameAr: string;
  branchNameFr: string;
  branchNameEn: string;
  units: number;
  unitsInStock: number;
  cost: number;
  accumulatedDepreciation: number;
  netBookValue: number;
  usefulLifeMonths: number | null;
  monthlyCharge: number;
  scrapQty: number;
  scrapValue: number;
  /** The method the type is depreciated by (straight line unless the accountant chose otherwise). */
  method?: DepreciationMethod;
  decliningCoefficient?: number | null;
  switchToStraightLine?: boolean;
}

export interface FixedAssetRegister {
  active: boolean;
  switchMonth: string | null;
  year: number;
  month: number;
  monthToDate: boolean;
  branchId: string | null;
  pools: FixedAssetPool[];
  cost: number;
  accumulatedDepreciation: number;
  netBookValue: number;
  units: number;
  postedThrough: string | null;
}

export interface FixedAssetHistoryLine {
  atUtc: string;
  kind: string;
  qty: number;
  cost: number;
  depreciation: number;
  otherBranchId: string | null;
  otherBranchCode: string | null;
  documentNumber: string | null;
  reason: string | null;
  by: string | null;
  posted: boolean;
  /** Phase M7d: a capitalised repair's piece reference, when its job named one. */
  reference?: string | null;
}

export interface FixedAssetHistory {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  branchId: string;
  branchCode: string;
  pool: FixedAssetPool | null;
  lines: FixedAssetHistoryLine[];
  /** The pool's schedule year by year, from the switch-over to the end of its life. */
  schedule?: FixedAssetScheduleYear[] | null;
}

/**
 * One calendar year of a pool's schedule: the net book value at the start, the year's depreciation, what events added or took
 * out at net book value, the value at the end; `switches`: a declining-balance layer turns to straight line that year;
 * `projected`: the year ends after the last month ended.
 */
export interface FixedAssetScheduleYear {
  year: number;
  opening: number;
  charge: number;
  movements: number;
  closing: number;
  switches: boolean;
  projected: boolean;
}

/** One month's depreciation as the practice ledger shows it: computed on read, posted by the accountant. */
export interface FixedAssetDepreciation {
  year: number;
  month: number;
  monthToDate: boolean;
  branchId: string | null;
  active: boolean;
  switchMonth: string | null;
  entries: PracticeEntry[];
  accounts: PracticeAccount[];
  checks: { code: string; passed: boolean; expected: number; actual: number }[];
  unmappedSlots: string[];
  notes: string[];
  totalDebit: number;
  totalCredit: number;
  postings: PracticePosting[];
}

/** `/api/fixed-assets` (REN-12, Phase M7a). */
@Injectable({ providedIn: 'root' })
export class FixedAssetsApi {
  private readonly api = inject(ApiService);

  settings(): Promise<FixedAssetSettings> {
    return this.api.getJson<FixedAssetSettings>('/api/fixed-assets/settings');
  }

  saveSettings(body: FixedAssetSettingsUpdate): Promise<FixedAssetSettings> {
    return this.api.putJson<FixedAssetSettings>('/api/fixed-assets/settings', body);
  }

  saveAccountMap(lines: { slot: string; ledgerAccountId: string | null }[]): Promise<FixedAssetSettings> {
    return this.api.putJson<FixedAssetSettings>('/api/fixed-assets/account-map', { lines });
  }

  switchOver(): Promise<FixedAssetSwitch> {
    return this.api.getJson<FixedAssetSwitch>('/api/fixed-assets/switch-over');
  }

  postSwitchOver(expectedStockValue: number, expectedOpeningValue: number): Promise<FixedAssetSwitch> {
    return this.api.postJson<FixedAssetSwitch>('/api/fixed-assets/switch-over', { expectedStockValue, expectedOpeningValue });
  }

  register(year: number | null, month: number | null, branchId: string | null): Promise<FixedAssetRegister> {
    return this.api.getJson<FixedAssetRegister>(`/api/fixed-assets/register${query(year, month, branchId)}`);
  }

  history(productId: string, branchId: string): Promise<FixedAssetHistory> {
    const q = new URLSearchParams({ productId, branchId });
    return this.api.getJson<FixedAssetHistory>(`/api/fixed-assets/register/history?${q}`);
  }

  depreciation(year: number | null, month: number | null, branchId: string | null): Promise<FixedAssetDepreciation> {
    return this.api.getJson<FixedAssetDepreciation>(`/api/fixed-assets/depreciation${query(year, month, branchId)}`);
  }

  post(year: number, month: number): Promise<FixedAssetDepreciation> {
    return this.api.postJson<FixedAssetDepreciation>('/api/fixed-assets/depreciation/post', { year, month });
  }

  reverse(year: number, month: number): Promise<FixedAssetDepreciation> {
    return this.api.postJson<FixedAssetDepreciation>('/api/fixed-assets/depreciation/reverse', { year, month });
  }
}

function query(year: number | null, month: number | null, branchId: string | null): string {
  const q = new URLSearchParams();
  if (year !== null && month !== null) {
    q.set('year', String(year));
    q.set('month', String(month));
  }
  if (branchId) q.set('branchId', branchId);
  const s = q.toString();
  return s ? `?${s}` : '';
}
