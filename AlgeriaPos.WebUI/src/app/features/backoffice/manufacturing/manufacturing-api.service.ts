import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/**
 * Manufacturing, Phase 1 (`AlgeriaPos.API/Endpoints/ManufacturingEndpoints.cs`): production lines,
 * materials, units, job roles, stage templates, bills of materials and who holds the permissions.
 * Configuration only — nothing here moves stock. The shapes mirror `ManufacturingDtos.cs`.
 */

export type ManufacturingRole = 'None' | 'RawMaterial' | 'Consumable' | 'Accessory' | 'SparePart' | 'SemiFinished' | 'Finished';
export const MANUFACTURING_ROLES: readonly ManufacturingRole[] = [
  'None',
  'RawMaterial',
  'Consumable',
  'Accessory',
  'SparePart',
  'SemiFinished',
  'Finished',
];
/** What a recipe may consume (`ManufacturingRoles.IsComponent`). */
export const COMPONENT_ROLES: readonly ManufacturingRole[] = ['RawMaterial', 'Consumable', 'Accessory', 'SparePart', 'SemiFinished'];
/** What a recipe may make (`ManufacturingRoles.IsProduced`). */
export const PRODUCED_ROLES: readonly ManufacturingRole[] = ['SemiFinished', 'Finished'];

export type UnitDimension = 'Count' | 'Mass' | 'Length' | 'Volume' | 'Area' | 'Other';
export const UNIT_DIMENSIONS: readonly UnitDimension[] = ['Count', 'Mass', 'Length', 'Volume', 'Area', 'Other'];

export type BomStatus = 'Draft' | 'Active' | 'Retired';
export type LineMemberRole = 'Manager' | 'Supervisor';

/** The three names every manufacturing record carries. */
export interface Named {
  nameAr: string;
  nameFr: string;
  nameEn: string;
}

export interface UnitDto extends Named {
  id: string;
  code: string;
  dimension: UnitDimension;
  factorToReference: number;
  isSystem: boolean;
  isActive: boolean;
}

export interface UpsertUnitRequest {
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string | null;
  dimension: UnitDimension;
  factorToReference: number;
  isActive: boolean;
}

export interface UnitConversion {
  quantity: number;
  fromUnitId: string;
  toUnitId: string;
  result: number;
  factor: number;
}

export type SpecKind = 'Measure' | 'Text';

/** A specification field: measured in a unit of its dimension, or written as a word. */
export interface SpecFieldDto extends Named {
  id: string;
  code: string;
  kind: SpecKind;
  dimension: UnitDimension;
  isSystem: boolean;
  isActive: boolean;
  sortOrder: number;
  /** A value names it: its kind and dimension are fixed. */
  inUse: boolean;
}

export interface UpsertSpecFieldRequest {
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string | null;
  kind: SpecKind;
  dimension: UnitDimension | null;
  isActive: boolean;
  sortOrder: number;
}

export interface SpecValueDto {
  attributeId: string;
  attributeCode: string;
  kind: SpecKind;
  value: number | null;
  unitId: string | null;
  unitCode: string | null;
  text: string | null;
}

export interface SpecValueRequest {
  attributeId: string;
  value: number | null;
  unitId: string | null;
  text: string | null;
}

export interface ProductUnitDto {
  unitId: string;
  unitCode: string;
  factorToBase: number;
  forPurchase: boolean;
  forProduction: boolean;
  forSale: boolean;
}

export interface MaterialDto extends Named {
  productId: string;
  sku: string;
  isActive: boolean;
  isRentable: boolean;
  role: ManufacturingRole;
  soldAtTill: boolean;
  baseUnitId: string | null;
  baseUnitCode: string | null;
  unit: string;
  units: ProductUnitDto[];
  /** A manufacturing-only item sold as well; soldAtTill already counts it. */
  alsoForSale: boolean;
  specs: SpecValueDto[];
}

export interface ClassifyMaterialRequest {
  role: ManufacturingRole;
  baseUnitId: string | null;
  units: { unitId: string; factorToBase: number; forPurchase: boolean; forProduction: boolean; forSale: boolean }[];
  alsoForSale: boolean;
  /** The whole specification; the server keeps the stored one when this is absent. */
  specs: SpecValueRequest[];
}

export interface JobRoleDto extends Named {
  id: string;
  code: string;
  description: string | null;
  isActive: boolean;
}

export interface UpsertJobRoleRequest {
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string | null;
  description: string | null;
  isActive: boolean;
}

export interface StageRoleDto {
  jobRoleId: string;
  jobRoleCode: string;
  count: number;
}

export interface StageDto extends Named {
  id: string;
  sequence: number;
  estimatedMinutes: number;
  expectedWorkers: number;
  isQualityCheckpoint: boolean;
  qualityNote: string | null;
  isOutput: boolean;
  roles: StageRoleDto[];
}

export interface StageRequest {
  nameAr: string;
  nameFr: string;
  nameEn: string | null;
  estimatedMinutes: number;
  expectedWorkers: number;
  isQualityCheckpoint: boolean;
  qualityNote: string | null;
  isOutput: boolean;
  roles: { jobRoleId: string; count: number }[];
}

export interface StageTemplateDto extends Named {
  id: string;
  code: string;
  description: string | null;
  productId: string | null;
  productSku: string | null;
  isActive: boolean;
  steps: StageDto[];
}

export interface UpsertStageTemplateRequest {
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string | null;
  description: string | null;
  productId: string | null;
  isActive: boolean;
  steps: StageRequest[];
}

export interface LineMemberDto {
  userId: string;
  username: string;
  fullName: string;
  role: LineMemberRole;
}

export interface LineEmployeeDto {
  id: string;
  employeeId: string;
  number: string;
  fullName: string;
  payrollBranchId: string;
  payrollBranchCode: string;
  payrollBranchNameAr: string;
  payrollBranchNameFr: string;
  jobTitle: string | null;
  jobRoleId: string | null;
  jobRoleCode: string | null;
  assignedOn: string;
  endedOn: string | null;
  note: string | null;
}

export interface LineProductDto extends Named {
  productId: string;
  sku: string;
  role: ManufacturingRole;
}

export interface ProductionLineSummary extends Named {
  id: string;
  code: string;
  description: string | null;
  locationId: string;
  locationCode: string;
  locationNameAr: string;
  locationNameFr: string;
  managerUserId: string | null;
  managerName: string | null;
  isActive: boolean;
  employeeCount: number;
  productCount: number;
  activeBomCount: number;
  canEdit: boolean;
  canAssign: boolean;
}

export interface ProductionLineDto {
  line: ProductionLineSummary;
  members: LineMemberDto[];
  employees: LineEmployeeDto[];
  products: LineProductDto[];
}

export interface UpsertProductionLineRequest {
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string | null;
  description: string | null;
  locationId: string;
  managerUserId: string | null;
  isActive: boolean;
}

export interface LineStockRow extends Named {
  productId: string;
  sku: string;
  role: ManufacturingRole;
  unit: string;
  available: number;
  maintenance: number;
  scrap: number;
  reorderLevel: number;
}

export interface LineLocation {
  id: string;
  code: string;
  nameAr: string;
  nameFr: string;
  isActive: boolean;
  lineId: string | null;
}

export interface LineUserCandidate {
  id: string;
  username: string;
  fullName: string;
  branchId: string | null;
}

export interface LineEmployeeCandidate {
  id: string;
  number: string;
  fullName: string;
  branchId: string;
  branchCode: string;
  branchNameAr: string;
  branchNameFr: string;
  jobTitle: string | null;
}

export interface BomLineDto {
  id: string;
  sequence: number;
  componentProductId: string;
  componentSku: string;
  componentNameAr: string;
  componentNameFr: string;
  componentNameEn: string;
  componentRole: ManufacturingRole;
  quantity: number;
  unitId: string;
  unitCode: string;
  wastePercent: number;
  specification: string | null;
  note: string | null;
  specs: SpecValueDto[];
}

export interface BomSummary {
  id: string;
  productId: string;
  productSku: string;
  productNameAr: string;
  productNameFr: string;
  productNameEn: string;
  productionLineId: string | null;
  productionLineCode: string | null;
  version: number;
  status: BomStatus;
  batchQuantity: number;
  batchUnitId: string;
  batchUnitCode: string;
  lineCount: number;
  stageCount: number;
  createdAtUtc: string;
  activatedAtUtc: string | null;
  retiredAtUtc: string | null;
}

export interface BomDto {
  bom: BomSummary;
  stageTemplateId: string | null;
  notes: string | null;
  createdBy: string | null;
  activatedBy: string | null;
  retiredBy: string | null;
  lines: BomLineDto[];
  stages: StageDto[];
  canEdit: boolean;
  canApprove: boolean;
}

export interface BomLineRequest {
  componentProductId: string;
  quantity: number;
  unitId: string;
  wastePercent: number;
  specification: string | null;
  note: string | null;
  specs: SpecValueRequest[];
}

export interface UpsertBomRequest {
  productId: string;
  productionLineId: string | null;
  batchQuantity: number;
  batchUnitId: string;
  stageTemplateId: string | null;
  notes: string | null;
  lines: BomLineRequest[];
  /** Null with a template: the template's stages are copied. */
  stages: StageRequest[] | null;
}

export interface BomRequirementLine {
  bomLineId: string;
  componentProductId: string;
  componentSku: string;
  componentNameAr: string;
  componentNameFr: string;
  componentNameEn: string;
  unitId: string;
  unitCode: string;
  netQuantity: number;
  wasteQuantity: number;
  grossQuantity: number;
  baseUnitId: string;
  baseUnitCode: string;
  baseQuantity: number;
}

export interface BomRequirements {
  bomId: string;
  plannedQuantity: number;
  plannedUnitId: string;
  plannedUnitCode: string;
  batches: number;
  lines: BomRequirementLine[];
  totals: { componentProductId: string; componentSku: string; baseUnitId: string; baseUnitCode: string; baseQuantity: number }[];
}

function query(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== null && value !== undefined && value !== '') search.set(key, String(value));
  const text = search.toString();
  return text ? `?${text}` : '';
}

const BASE = '/api/manufacturing';

@Injectable({ providedIn: 'root' })
export class ManufacturingApiService {
  private readonly api = inject(ApiService);

  // ---------- units ----------
  units(): Promise<UnitDto[]> {
    return this.api.getJson<UnitDto[]>(`${BASE}/units`);
  }
  saveUnit(id: string | null, req: UpsertUnitRequest): Promise<UnitDto> {
    return id ? this.api.putJson<UnitDto>(`${BASE}/units/${id}`, req) : this.api.postJson<UnitDto>(`${BASE}/units`, req);
  }
  convert(quantity: number, fromUnitId: string, toUnitId: string, productId?: string | null): Promise<UnitConversion> {
    return this.api.getJson<UnitConversion>(`${BASE}/units/convert${query({ quantity, fromUnitId, toUnitId, productId })}`);
  }

  // ---------- specification fields ----------
  specFields(): Promise<SpecFieldDto[]> {
    return this.api.getJson<SpecFieldDto[]>(`${BASE}/spec-fields`);
  }
  saveSpecField(id: string | null, req: UpsertSpecFieldRequest): Promise<SpecFieldDto> {
    return id
      ? this.api.putJson<SpecFieldDto>(`${BASE}/spec-fields/${id}`, req)
      : this.api.postJson<SpecFieldDto>(`${BASE}/spec-fields`, req);
  }

  // ---------- materials ----------
  materials(role?: ManufacturingRole | null, includeInactive = false): Promise<MaterialDto[]> {
    return this.api.getJson<MaterialDto[]>(`${BASE}/materials${query({ role, includeInactive: includeInactive || null })}`);
  }
  classify(productId: string, req: ClassifyMaterialRequest): Promise<MaterialDto> {
    return this.api.putJson<MaterialDto>(`${BASE}/materials/${productId}`, req);
  }

  // ---------- job roles ----------
  jobRoles(): Promise<JobRoleDto[]> {
    return this.api.getJson<JobRoleDto[]>(`${BASE}/job-roles`);
  }
  saveJobRole(id: string | null, req: UpsertJobRoleRequest): Promise<JobRoleDto> {
    return id ? this.api.putJson<JobRoleDto>(`${BASE}/job-roles/${id}`, req) : this.api.postJson<JobRoleDto>(`${BASE}/job-roles`, req);
  }

  // ---------- stage templates ----------
  stageTemplates(productId?: string | null): Promise<StageTemplateDto[]> {
    return this.api.getJson<StageTemplateDto[]>(`${BASE}/stage-templates${query({ productId })}`);
  }
  saveStageTemplate(id: string | null, req: UpsertStageTemplateRequest): Promise<StageTemplateDto> {
    return id
      ? this.api.putJson<StageTemplateDto>(`${BASE}/stage-templates/${id}`, req)
      : this.api.postJson<StageTemplateDto>(`${BASE}/stage-templates`, req);
  }

  // ---------- lines ----------
  lines(includeInactive = false): Promise<ProductionLineSummary[]> {
    return this.api.getJson<ProductionLineSummary[]>(`${BASE}/lines${query({ includeInactive: includeInactive || null })}`);
  }
  line(id: string): Promise<ProductionLineDto> {
    return this.api.getJson<ProductionLineDto>(`${BASE}/lines/${id}`);
  }
  saveLine(id: string | null, req: UpsertProductionLineRequest): Promise<ProductionLineDto> {
    return id ? this.api.putJson<ProductionLineDto>(`${BASE}/lines/${id}`, req) : this.api.postJson<ProductionLineDto>(`${BASE}/lines`, req);
  }
  setMembers(id: string, members: { userId: string; role: LineMemberRole }[]): Promise<ProductionLineDto> {
    return this.api.putJson<ProductionLineDto>(`${BASE}/lines/${id}/members`, { members });
  }
  setProducts(id: string, productIds: string[]): Promise<ProductionLineDto> {
    return this.api.putJson<ProductionLineDto>(`${BASE}/lines/${id}/products`, { productIds });
  }
  assignEmployee(id: string, req: { employeeId: string; jobRoleId: string | null; assignedOn: string | null; note: string | null }): Promise<ProductionLineDto> {
    return this.api.postJson<ProductionLineDto>(`${BASE}/lines/${id}/employees`, req);
  }
  endEmployee(id: string, assignmentId: string, endedOn: string | null): Promise<ProductionLineDto> {
    return this.api.postJson<ProductionLineDto>(`${BASE}/lines/${id}/employees/${assignmentId}/end`, { endedOn });
  }
  lineStock(id: string): Promise<LineStockRow[]> {
    return this.api.getJson<LineStockRow[]>(`${BASE}/lines/${id}/stock`);
  }
  employeeCandidates(id: string): Promise<LineEmployeeCandidate[]> {
    return this.api.getJson<LineEmployeeCandidate[]>(`${BASE}/lines/${id}/employee-candidates`);
  }
  lineLocations(): Promise<LineLocation[]> {
    return this.api.getJson<LineLocation[]>(`${BASE}/line-locations`);
  }
  lineUserCandidates(): Promise<LineUserCandidate[]> {
    return this.api.getJson<LineUserCandidate[]>(`${BASE}/line-user-candidates`);
  }

  // ---------- bills of materials ----------
  boms(filter: { productId?: string | null; lineId?: string | null; status?: BomStatus | null } = {}): Promise<BomSummary[]> {
    return this.api.getJson<BomSummary[]>(`${BASE}/boms${query(filter)}`);
  }
  bom(id: string): Promise<BomDto> {
    return this.api.getJson<BomDto>(`${BASE}/boms/${id}`);
  }
  createBom(req: UpsertBomRequest): Promise<BomDto> {
    return this.api.postJson<BomDto>(`${BASE}/boms`, req);
  }
  updateBom(id: string, req: UpsertBomRequest): Promise<BomDto> {
    return this.api.putJson<BomDto>(`${BASE}/boms/${id}`, req);
  }
  deleteBom(id: string): Promise<void> {
    return this.api.deleteJson<void>(`${BASE}/boms/${id}`);
  }
  activateBom(id: string): Promise<BomDto> {
    return this.api.postJson<BomDto>(`${BASE}/boms/${id}/activate`, {});
  }
  retireBom(id: string): Promise<BomDto> {
    return this.api.postJson<BomDto>(`${BASE}/boms/${id}/retire`, {});
  }
  newBomVersion(id: string): Promise<BomDto> {
    return this.api.postJson<BomDto>(`${BASE}/boms/${id}/new-version`, {});
  }
  requirements(id: string, quantity: number, unitId?: string | null): Promise<BomRequirements> {
    return this.api.getJson<BomRequirements>(`${BASE}/boms/${id}/requirements${query({ quantity, unitId })}`);
  }
}
