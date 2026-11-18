import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/**
 * Manufacturing, Phase 2 (ManufacturingOperationEndpoints): production orders, their materials and output,
 * the daily production record and its hours, and the manufacturing reports. Shapes mirror
 * `Application/Contracts/ManufacturingOrderDtos.cs`; every quantity named `…BaseQty` is in the product's
 * base unit. Money figures are reference costs as they stood, never postings.
 */

export type ProductionOrderStatus = 'Open' | 'OnHold' | 'CompletionRequested' | 'Completed' | 'Cancelled';
export type ProductionLogStatus = 'Draft' | 'Submitted' | 'Approved';
export type MaterialMovementKind = 'Issue' | 'Return' | 'Scrap';
export type ConsumptionReason = 'Used' | 'Offcut' | 'CountDifference';
export type NonOrderCategory = 'Cleaning' | 'Breakdown' | 'WaitingForMaterial' | 'Training' | 'Other';
export type StaleWord = 'stale' | 'after_withdrawal' | 'unknown';

export const ORDER_STATUSES: ProductionOrderStatus[] = ['Open', 'OnHold', 'CompletionRequested', 'Completed', 'Cancelled'];
export const NON_ORDER_CATEGORIES: NonOrderCategory[] = ['Cleaning', 'Breakdown', 'WaitingForMaterial', 'Training', 'Other'];

export interface CreateOrderRequest {
  lineId: string;
  productId: string;
  plannedQty: number;
  plannedUnitId: string;
  plannedStart: string;
  plannedEnd: string;
  notes: string | null;
  clientRequestId: string;
  startShort: boolean;
  shortageReason: string | null;
}

export interface MaterialAvailability {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  baseUnitCode: string;
  requiredBaseQty: number;
  stockBaseQty: number;
  reservedByOthersBaseQty: number;
  freeBaseQty: number;
  shortBaseQty: number;
}

export interface OrderPreview {
  bomId: string;
  bomVersion: number;
  plannedBaseQty: number;
  baseUnitCode: string;
  batches: number;
  materials: MaterialAvailability[];
  short: boolean;
}

export interface ProductionOrderSummary {
  id: string;
  number: string;
  lineId: string;
  lineCode: string;
  productId: string;
  productSku: string;
  productNameAr: string;
  productNameFr: string;
  productNameEn: string;
  status: ProductionOrderStatus;
  plannedBaseQty: number;
  baseUnitCode: string;
  acceptedBaseQty: number;
  rejectedBaseQty: number;
  plannedStart: string;
  plannedEnd: string;
  actualEnd: string | null;
  overdueDays: number;
  late: boolean;
  startedShort: boolean;
  createdAtUtc: string;
}

export interface OrderMaterial {
  id: string;
  sequence: number;
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  role: string;
  baseUnitId: string;
  baseUnitCode: string;
  netBaseQty: number;
  wasteBaseQty: number;
  requiredBaseQty: number;
  estimatedUnitCost: number;
  specification: string | null;
  issuedBaseQty: number;
  returnedBaseQty: number;
  consumedBaseQty: number;
  scrappedBaseQty: number;
  reservationReleasedBaseQty: number;
  reservedBaseQty: number;
  atLineBaseQty: number;
}

export interface OrderStage {
  id: string;
  sequence: number;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  plannedMinutes: number;
  expectedWorkers: number;
  isQualityCheckpoint: boolean;
  isOutput: boolean;
  goodBaseQty: number;
  rejectedBaseQty: number;
  bookedHours: number;
}

export interface OrderEvent {
  kind: string;
  detail: string | null;
  userName: string | null;
  atUtc: string;
}

export interface MaterialMovementLine {
  orderMaterialId: string;
  productId: string;
  sku: string;
  enteredQty: number;
  unitCode: string;
  baseQty: number;
  unitCostSnapshot: number;
}

export interface MaterialMovement {
  id: string;
  number: string;
  kind: MaterialMovementKind;
  postingState: string;
  note: string | null;
  userName: string | null;
  atUtc: string;
  lines: MaterialMovementLine[];
}

export interface Consumption {
  id: string;
  orderMaterialId: string;
  sku: string;
  logRowId: string | null;
  baseQty: number;
  reason: ConsumptionReason;
  note: string | null;
  userName: string | null;
  atUtc: string;
}

export interface OutputRelease {
  id: string;
  number: string;
  enteredQty: number;
  unitCode: string;
  baseQty: number;
  unitCostSnapshot: number;
  postingState: string;
  note: string | null;
  userName: string | null;
  atUtc: string;
}

export interface OrderExpense {
  id: string;
  description: string;
  amount: number;
  paymentVoucherId: string | null;
  userName: string | null;
  atUtc: string;
}

export interface ProductionOrder {
  order: ProductionOrderSummary;
  bomId: string;
  bomVersion: number;
  plannedQty: number;
  plannedUnitCode: string;
  batches: number;
  shortageReason: string | null;
  notes: string | null;
  estimatedMaterialCost: number;
  producedBaseQty: number;
  awaitingReleaseBaseQty: number;
  remainingBaseQty: number;
  createdBy: string | null;
  completionRequestedBy: string | null;
  completionRequestedAtUtc: string | null;
  completedBy: string | null;
  completedAtUtc: string | null;
  materials: OrderMaterial[];
  stages: OrderStage[];
  movements: MaterialMovement[];
  consumptions: Consumption[];
  releases: OutputRelease[];
  expenses: OrderExpense[];
  events: OrderEvent[];
  canManage: boolean;
  canMoveMaterials: boolean;
  canRelease: boolean;
  canRequestCompletion: boolean;
  canApproveCompletion: boolean;
}

export interface CostMaterial {
  orderMaterialId: string;
  sku: string;
  role: string;
  baseUnitCode: string;
  requiredBaseQty: number;
  estimatedUnitCost: number;
  estimatedCost: number;
  issuedBaseQty: number;
  returnedBaseQty: number;
  consumedBaseQty: number;
  scrappedBaseQty: number;
  usedBaseQty: number;
  usedCostAtSnapshot: number;
}

export interface CostLabour {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  stageId: string | null;
  hours: number;
  payrollBranchId: string;
  payrollYear: number;
  payrollMonth: number;
  payrollRunId: string | null;
  payrollRunNumber: string | null;
  payrollRunStatus: string | null;
}

export interface CostInputs {
  orderId: string;
  number: string;
  plannedBaseQty: number;
  acceptedBaseQty: number;
  rejectedBaseQty: number;
  estimatedMaterialCost: number;
  materials: CostMaterial[];
  labour: CostLabour[];
  labourHours: number;
  expenses: OrderExpense[];
  expensesTotal: number;
}

export interface LaborBooking {
  id: string;
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  hours: number;
  attendanceHours: number;
  overrideReason: string | null;
  overriddenBy: string | null;
  userName: string | null;
  atUtc: string;
  /** C-05: how many of the hours were overtime; 0 when none were marked. */
  overtimeHours: number;
}

export interface ProductionLogRow {
  id: string;
  sequence: number;
  orderId: string | null;
  orderNumber: string | null;
  stageId: string | null;
  stageNameAr: string | null;
  stageNameFr: string | null;
  stageNameEn: string | null;
  isOutputStage: boolean;
  category: NonOrderCategory | null;
  productId: string | null;
  productSku: string | null;
  baseUnitCode: string | null;
  specification: string | null;
  plannedQty: number;
  completedQty: number;
  rejectedQty: number;
  rejectReason: string | null;
  notes: string | null;
  bookings: LaborBooking[];
  consumptions: Consumption[];
}

export interface ProductionLogSummary {
  id: string;
  number: string;
  lineId: string;
  lineCode: string;
  day: string;
  status: ProductionLogStatus;
  rowCount: number;
  hours: number;
  submittedBy: string | null;
  approvedBy: string | null;
}

export interface ProductionLogWarning {
  code: string;
  orderNumber: string;
  stageNameAr: string;
  stageNameFr: string;
  stageNameEn: string;
  previousNameAr: string;
  previousNameFr: string;
  previousNameEn: string;
  stageGood: number;
  previousGood: number;
}

export interface ProductionLog {
  log: ProductionLogSummary;
  notes: string | null;
  returnedReason: string | null;
  createdBy: string | null;
  submittedAtUtc: string | null;
  approvedAtUtc: string | null;
  rows: ProductionLogRow[];
  warnings: ProductionLogWarning[];
  canEdit: boolean;
  canApprove: boolean;
}

export interface LogRowRequest {
  id: string | null;
  orderId: string | null;
  stageId: string | null;
  category: NonOrderCategory | null;
  specification: string | null;
  plannedQty: number;
  completedQty: number;
  rejectedQty: number;
  rejectReason: string | null;
  notes: string | null;
}

export interface LogEmployeeCandidate {
  employeeId: string;
  number: string;
  fullName: string;
  attendanceHours: number | null;
  attendanceSource: string;
  bookedHours: number;
  payrollLocked: boolean;
}

export interface PosExceptionRow {
  id: string;
  saleId: string;
  reference: string;
  soldAtUtc: string;
  receivedAtUtc: string;
  branchId: string;
  branchCode: string;
  branchNameAr: string;
  branchNameFr: string;
  deviceCode: string;
  productId: string;
  sku: string;
  productNameAr: string;
  productNameFr: string;
  productNameEn: string;
  quantity: number;
  role: string;
  cashierName: string | null;
  withdrawnAtUtc: string | null;
  stale: StaleWord;
}

export interface PosExceptionReport {
  rows: PosExceptionRow[];
  total: number;
  totalQuantity: number;
  staleCount: number;
  afterWithdrawalCount: number;
  unknownCount: number;
}

export interface PendingMovementRow {
  document: string;
  number: string;
  kind: string;
  orderId: string;
  orderNumber: string;
  lineId: string;
  lineCode: string;
  productId: string;
  sku: string;
  baseQty: number;
  /** A material line's reference cost; a release's costing-preview unit value, or null when not valued. */
  unitCostSnapshot: number | null;
  value: number | null;
  direction: string;
  userName: string | null;
  atUtc: string;
}

export interface PendingMovements {
  rows: PendingMovementRow[];
  issuedValue: number;
  returnedValue: number;
  /** Null when releases are not valued: the server does not compute costs, or the reader may not see them. */
  releasedValue: number | null;
  workInProgressValue: number;
  releasesValued: boolean;
}

// ---------- costing, Phase A (read-only previews; docs/manufacturing/costing-plan.md) ----------

export type LabourBasis = 'Actual' | 'Standard' | 'None';

/** `labour` and `total` are null where the order's labour money is withheld from this reader (CostPrivacy). */
export interface CostElements {
  materials: number;
  consumables: number;
  accessories: number;
  labour: number | null;
  overhead: number;
  expenses: number;
  recovery: number;
  total: number | null;
}

/**
 * The estimate by element. `labour` and `total` are null for a reader without both rate keys when the
 * crew's standard rate is one employee's (`labourEstimateWithheld`).
 */
export interface CostEstimate {
  materials: number;
  consumables: number;
  accessories: number;
  labour: number | null;
  overhead: number;
  expenses: number;
  recovery: number;
  total: number | null;
}

export interface CostMaterialLine {
  id: string;
  sku: string;
  role: string;
  baseUnitCode: string;
  requiredBaseQty: number;
  estimatedUnitCost: number;
  estimatedValue: number;
  netBaseQty: number;
  netValue: number;
  issueAverage: number;
  consumedValue: number;
  scrapValue: number;
  atLineBaseQty: number;
  atLineValue: number;
  standardBaseQty: number | null;
  usageVariance: number | null;
  priceVariance: number | null;
}

/**
 * `rate` and `cost` are null unless the reader holds payroll.manage and the rates key; `hours` too, for
 * that reader, when one employee worked the order (`labourHoursWithheld`).
 */
export interface CostLabourLine {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  year: number;
  month: number;
  hours: number | null;
  basis: LabourBasis;
  reason: string | null;
  rate: number | null;
  cost: number | null;
  /** The payroll it comes from carries no employer social contribution: the cost leaves it out (D11). */
  withoutEmployerSocial: boolean;
  /**
   * C-05: how many of the hours were marked overtime (null with the hours) and what one costs (null without the rate
   * keys) — `rate` itself under the blended choice, `rate` plus the month's premium per hour under "premium to overtime".
   */
  overtimeHours?: number | null;
  overtimeRate?: number | null;
}

/** `ProductionCostEngine.Element*`: only approved, recorded and none are final. */
export type CostElementStatus =
  | 'approved' | 'recorded' | 'none' | 'reference_cost' | 'provisional' | 'incomplete' | 'not_set' | 'not_recorded';

export interface CostElementStatuses {
  materials: CostElementStatus;
  consumables: CostElementStatus;
  accessories: CostElementStatus;
  labour: CostElementStatus;
  overhead: CostElementStatus;
  expenses: CostElementStatus;
  recovery: CostElementStatus;
}

export interface CostRelease {
  id: string;
  number: string;
  baseQty: number;
  /** Null where the order's labour money is withheld: a release's value is the order's cost less the others. */
  value: number | null;
  unitValue: number | null;
  carriesRemainder: boolean;
}

/**
 * `labour` is efficiency and rate together; the split comes only with both rate keys. Without them,
 * `labour` and `total` are null too when the crew's standard rate is one employee's.
 */
export interface CostVariances {
  materialUsage: number;
  materialPrice: number;
  labourEfficiency: number | null;
  labourRate: number | null;
  labour: number | null;
  overhead: number;
  expenses: number;
  recovery: number;
  total: number | null;
}

export interface CostNote {
  code: string;
  value: number | null;
}

export interface ProductionCostSheet {
  orderId: string;
  orderNumber: string;
  status: ProductionOrderStatus;
  baseUnitCode: string;
  plannedBaseQty: number;
  acceptedBaseQty: number;
  rejectedBaseQty: number;
  completed: boolean;
  estimate: CostEstimate;
  estimatedUnitCost: number | null;
  actual: CostElements;
  actualUnitCost: number | null;
  atLineValue: number;
  releasedValue: number | null;
  workInProgress: number | null;
  keptInCharges: number | null;
  labourHours: number | null;
  standardHours: number;
  standardHoursForOutput: number;
  standardRate: number | null;
  overheadPerLabourHour: number;
  perEmployeeMoneyShown: boolean;
  /** One employee worked the order and the reader lacks the rate keys: no hours are sent. */
  labourHoursWithheld: boolean;
  /** The reader lacks the rate keys: no labour estimate, nor the estimated total it is part of. */
  labourEstimateWithheld: boolean;
  /**
   * The reader lacks the rate keys (owner, 2026-11-03): the order's labour money and every figure that gives it back —
   * total and unit cost, released value, work in progress, what stays in charges, spoilage, the variance total — are null.
   */
  orderMoneyWithheld?: boolean;
  materials: CostMaterialLine[];
  labour: CostLabourLine[];
  releases: CostRelease[];
  variances: CostVariances | null;
  spoilage: number | null;
  notes: CostNote[];
  /** Approved only when every element rests on approved inputs. */
  costStatus: 'provisional' | 'approved';
  statuses: CostElementStatuses;
  employerSocialMissing: boolean;
}

export interface WorkInProgressRow {
  orderId: string;
  orderNumber: string;
  status: ProductionOrderStatus;
  lineId: string;
  lineCode: string;
  productSku: string;
  baseUnitCode: string;
  plannedBaseQty: number;
  acceptedBaseQty: number;
  /** Null where the order's labour money is withheld from this reader. */
  costToDate: number | null;
  releasedValue: number | null;
  atLineValue: number;
  workInProgress: number | null;
  provisional: boolean;
}

/** A line's live orders added up; `withheld` when, for a reader without rates, it rests on one order or one worker. */
export interface WorkInProgressLine {
  lineId: string;
  lineCode: string;
  orders: number;
  workers: number;
  costToDate: number | null;
  releasedValue: number | null;
  atLineValue: number;
  workInProgress: number | null;
  withheld: boolean;
}

export interface WorkInProgressReport {
  rows: WorkInProgressRow[];
  /** Null for a reader without rates when any line's sums are withheld: the total would give that line back. */
  costToDate: number | null;
  releasedValue: number | null;
  atLineValue: number;
  workInProgress: number | null;
  lines?: WorkInProgressLine[];
  /** False for a reader without the rate keys: the orders' own figures are then null. */
  moneyShown?: boolean;
}

export interface LabourAbsorptionRow {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  orderHours: number;
  otherHours: number;
  basis: LabourBasis;
  reason: string | null;
  paidHours: number | null;
  employerCost: number | null;
  rate: number | null;
  absorbedCost: number | null;
  unabsorbedCost: number | null;
  withoutEmployerSocial: boolean;
  /** C-05: order hours marked overtime. With the rate keys: overtime hours paid, the premium's employer cost, and — under
   *  "premium to overtime hours" — the part of it no order hour carried, left in the wage charges. */
  overtimeHours?: number;
  paidOvertimeHours?: number | null;
  overtimePremium?: number | null;
  premiumInCharges?: number | null;
}

export interface LabourAbsorptionReport {
  year: number;
  month: number;
  moneyShown: boolean;
  rows: LabourAbsorptionRow[];
  orderHours: number;
  otherHours: number;
  employerCost: number | null;
  absorbedCost: number | null;
  unabsorbedCost: number | null;
  employerSocialMissing: boolean;
  /** Each line's labour on its orders this month (owner, 2026-11-03). */
  lines?: LabourLineMonth[];
  /** C-05: the costing choice the report applied, and the premium left in the wage charges under the premium choice. */
  labourAbsorption?: 'BlendedActualRate' | 'PremiumToOvertime';
  premiumInCharges?: number | null;
}

/** One line's month; `withheld` when, for a reader without rates, it rests on one order or one worker. */
export interface LabourLineMonth {
  lineId: string;
  lineCode: string;
  orders: number;
  workers: number;
  orderHours: number;
  absorbedCost: number | null;
  withheld: boolean;
}

export interface MaterialFlowIssue {
  kind: string;
  reference: string;
  orderId: string | null;
  orderNumber: string | null;
  productId: string | null;
  sku: string | null;
  locationId: string | null;
  locationCode: string | null;
  expected: number | null;
  actual: number | null;
}

export interface MaterialFlowReport {
  movementsChecked: number;
  materialsChecked: number;
  stocksChecked: number;
  issues: MaterialFlowIssue[];
}

// ---------- costing B2: reconciliation of B1's frozen sheets (read only; docs/manufacturing/costing-b2.md) ----------

/** Original, late effects kept apart, adjusted provisional; financially finalized is always null ("not_implemented"). */
export interface CostFigures {
  original: number;
  lateRecorded: number;
  lateRecognized: number;
  lateUnclassified: number;
  lateUncertain: number;
  adjustedProvisional: number;
  financiallyFinalized: number | null;
  finalizationState: string;
  originalUnitCost: number | null;
}

export interface CostSheetHeader {
  sheetId: string;
  kind: 'created' | 'reconstructed' | string;
  sourceState: 'complete' | 'incompletelegacy' | string;
  calculationVersion: string;
  status: string;
  /** When the record was made — not an accounting date. */
  frozenAtUtc: string;
  frozenBy: string | null;
  completedAtUtc: string | null;
  freezeLagDays: number | null;
  sourceHash: string;
}

export interface CostElementReconciliation {
  element: string;
  status: string;
  original: number;
  lateRecognized: number;
  adjusted: number;
}

export interface LateCostRow {
  id: string;
  orderId: string;
  orderNumber: string;
  lineCode: string;
  kind: string;
  element: string | null;
  treatment: 'recognized' | 'unclassified' | 'uncertain' | string;
  recordedAmount: number;
  recognizedEffect: number;
  unclassifiedEffect: number;
  uncertainEffect: number;
  /** Null with `redacted` true when payroll-derived and the reader may not see rates. */
  sourceReference: string | null;
  reason: string | null;
  redacted: boolean;
  recordedAtUtc: string;
  flags: string[];
}

export interface CostCheck {
  code: string;
  result: 'pass' | 'fail' | 'not_applicable' | string;
  expected: number | null;
  actual: number | null;
  detail: string | null;
}

export interface CostGap {
  code: string;
  severity: string;
  element: string | null;
  value: number | null;
  detail: string | null;
}

export interface ReleaseReconciliationRow {
  releaseId: string;
  number: string;
  atUtc: string;
  quantity: number;
  value: number;
  unitValue: number;
  carriesRemainder: boolean;
  catalogueReferenceValue: number | null;
}

export interface ReleaseReconciliation {
  rows: ReleaseReconciliationRow[];
  acceptedQty: number;
  releasedQty: number;
  unreleasedAcceptedQty: number;
  releasedValue: number;
  keptInCharges: number;
  lateNotAllocated: number;
  flags: string[];
}

export interface ScrapCost {
  acceptedQty: number;
  rejectedQty: number;
  rejectionRatePercent: number | null;
  materialScrapValue: number | null;
  recoveryValue: number;
  recoveryStatus: string;
  spoilageAbsorbed: number | null;
  materialWastage: number | null;
  unavailable: string | null;
}

export interface VarianceLine {
  component: string;
  detail: boolean;
  original: number | null;
  lateRecognized: number | null;
  adjusted: number | null;
}

export interface CostVariance {
  available: boolean;
  reason: string | null;
  lines: VarianceLine[];
}

export interface LabourSnapshotRow {
  employeeNumber: string;
  employeeName: string;
  year: number;
  month: number;
  hours: number | null;
  basis: string;
  reason: string | null;
  rate: number | null;
  cost: number | null;
}

export interface LabourSnapshot {
  perEmployeeMoneyShown: boolean;
  hoursWithheld: boolean;
  employees: number;
  rows: LabourSnapshotRow[];
}

export interface CostElementDrift {
  element: string;
  adjusted: number;
  live: number;
  unexplained: number;
}

export interface LiveComparison {
  comparable: boolean;
  reason: string | null;
  original: number;
  lateRecognized: number;
  adjustedProvisional: number;
  live: number | null;
  unexplained: number | null;
  lateRecognizedAfterAsOf: number;
  lateUnclassified: number;
  lateUncertain: number;
  elements: CostElementDrift[];
  sourceChanges: CostGap[];
}

export interface OrderCostReconciliation {
  orderId: string;
  orderNumber: string;
  orderStatus: string;
  lineId: string;
  lineCode: string;
  productSku: string;
  baseUnitCode: string;
  state: 'frozen' | 'not_frozen' | 'not_frozen_at_date' | 'not_completed' | string;
  sheet: CostSheetHeader | null;
  asOf: string;
  acceptedBaseQty: number;
  rejectedBaseQty: number;
  figures: CostFigures | null;
  elements: CostElementReconciliation[];
  roundingResidual: number | null;
  lateCosts: LateCostRow[];
  releases: ReleaseReconciliation | null;
  scrap: ScrapCost | null;
  variances: CostVariance | null;
  labour: LabourSnapshot | null;
  checks: CostCheck[];
  gaps: CostGap[];
  limitations: string[];
  live: LiveComparison | null;
}

export interface CostReportPage {
  page: number;
  pageSize: number;
  total: number;
}

export interface CostReportPeriod {
  from: string;
  to: string;
  basis: string;
  asOf: string;
}

export interface CostHistoryRow {
  orderId: string;
  orderNumber: string;
  lineCode: string;
  productSku: string;
  state: string;
  sheet: CostSheetHeader | null;
  acceptedBaseQty: number;
  rejectedBaseQty: number;
  figures: CostFigures | null;
  lateCount: number;
  integrity: string | null;
  gapCodes: string[];
  live: { comparable: boolean; reason: string | null; live: number | null; unexplained: number | null } | null;
}

export interface CostHistoryReport {
  period: CostReportPeriod;
  rows: CostHistoryRow[];
  page: CostReportPage;
  totals: {
    frozen: number;
    notFrozen: number;
    reconstructed: number;
    incompleteLegacy: number;
    integrityFailures: number;
    original: number;
    lateRecorded: number;
    lateRecognized: number;
    lateUnclassified: number;
    lateUncertain: number;
    adjustedProvisional: number;
  };
  liveIncluded: boolean;
  limitations: string[];
}

export interface LateCostReport {
  period: CostReportPeriod;
  rows: LateCostRow[];
  page: CostReportPage;
  byElement: { element: string; recorded: number; recognized: number; unclassified: number; uncertain: number }[];
  recorded: number;
  recognized: number;
  unclassified: number;
  uncertain: number;
  limitations: string[];
}

export interface CostVarianceReport {
  period: CostReportPeriod;
  rows: { orderId: string; orderNumber: string; lineCode: string; acceptedBaseQty: number; variance: CostVariance }[];
  page: CostReportPage;
  totals: VarianceLine[];
  unavailable: number;
  limitations: string[];
}

export interface ReleaseReconciliationReport {
  period: CostReportPeriod;
  rows: { orderId: string; orderNumber: string; lineCode: string; original: number; reconciliation: ReleaseReconciliation }[];
  page: CostReportPage;
  original: number;
  releasedValue: number;
  keptInCharges: number;
  lateNotAllocated: number;
  limitations: string[];
}

export interface ScrapCostReport {
  period: CostReportPeriod;
  rows: { orderId: string; orderNumber: string; lineCode: string; scrap: ScrapCost }[];
  page: CostReportPage;
  acceptedQty: number;
  rejectedQty: number;
  materialScrapValue: number | null;
  spoilageAbsorbed: number | null;
  limitations: string[];
}

export interface CostGapReport {
  period: CostReportPeriod;
  rows: { orderId: string; orderNumber: string; lineCode: string; state: string; sheetId: string | null; integrity: string | null; checks: CostCheck[]; gaps: CostGap[] }[];
  page: CostReportPage;
  countByCode: Partial<Record<string, number>>;
  countBySeverity: Partial<Record<string, number>>;
  limitations: string[];
}

/** A register's filters; days are company days (`yyyy-MM-dd`), left empty for the server's default period. */
export interface CostReportFilter {
  lineId?: string | null;
  from?: string | null;
  to?: string | null;
  basis?: string | null;
  asOf?: string | null;
  page?: number | null;
  pageSize?: number | null;
  kind?: string | null;
  sourceState?: string | null;
  state?: string | null;
  element?: string | null;
  treatment?: string | null;
  flag?: string | null;
  code?: string | null;
  severity?: string | null;
  live?: boolean | null;
}

/** The six B2 registers, by their route under /reports. */
export type CostRegister = 'cost-history' | 'late-costs' | 'cost-variances' | 'release-reconciliation' | 'scrap-costs' | 'cost-gaps';

function query(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== null && value !== undefined && value !== '') search.set(key, String(value));
  const text = search.toString();
  return text ? `?${text}` : '';
}

/** A request id for an operational write: a retried request is the same document, never a second one. */
export function requestId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

const BASE = '/api/manufacturing';

@Injectable({ providedIn: 'root' })
export class ProductionApiService {
  private readonly api = inject(ApiService);

  operationsStatus(): Promise<{ stockOperationsEnabled: boolean; costingEnabled: boolean }> {
    return this.api.getJson(`${BASE}/operations/status`);
  }

  // ---------- orders ----------
  orders(filter: { lineId?: string | null; status?: ProductionOrderStatus | null; productId?: string | null } = {}): Promise<ProductionOrderSummary[]> {
    return this.api.getJson(`${BASE}/orders${query(filter)}`);
  }
  order(id: string): Promise<ProductionOrder> {
    return this.api.getJson(`${BASE}/orders/${id}`);
  }
  preview(req: CreateOrderRequest): Promise<OrderPreview> {
    return this.api.postJson(`${BASE}/orders/preview`, req);
  }
  create(req: CreateOrderRequest): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders`, req);
  }
  hold(id: string, reason: string): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/hold`, { reason });
  }
  resume(id: string): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/resume`, {});
  }
  cancel(id: string, reason: string): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/cancel`, { reason });
  }
  releaseReservation(id: string, materialId: string, baseQty: number, reason: string): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/materials/${materialId}/release-reservation`, { baseQty, reason });
  }
  addExpense(id: string, description: string, amount: number): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/expenses`, { description, amount, paymentVoucherId: null, clientRequestId: requestId() });
  }
  move(id: string, kind: MaterialMovementKind, lines: { orderMaterialId: string; qty: number; unitId: string }[], note: string | null): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/movements`, { kind, lines, note, clientRequestId: requestId() });
  }
  consume(id: string, orderMaterialId: string, baseQty: number, reason: ConsumptionReason, logRowId: string | null, note: string | null): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/consumptions`, { orderMaterialId, baseQty, reason, logRowId, note, clientRequestId: requestId() });
  }
  release(id: string, qty: number, unitId: string, note: string | null): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/releases`, { qty, unitId, note, clientRequestId: requestId() });
  }
  requestCompletion(id: string): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/request-completion`, {});
  }
  approveCompletion(id: string): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/approve-completion`, {});
  }
  refuseCompletion(id: string, reason: string): Promise<ProductionOrder> {
    return this.api.postJson(`${BASE}/orders/${id}/refuse-completion`, { reason });
  }
  costInputs(id: string): Promise<CostInputs> {
    return this.api.getJson(`${BASE}/orders/${id}/cost-inputs`);
  }

  // ---------- daily production records ----------
  logs(filter: { lineId?: string | null; from?: string | null; to?: string | null; status?: ProductionLogStatus | null } = {}): Promise<ProductionLogSummary[]> {
    return this.api.getJson(`${BASE}/logs${query(filter)}`);
  }
  log(id: string): Promise<ProductionLog> {
    return this.api.getJson(`${BASE}/logs/${id}`);
  }
  openLog(lineId: string, day: string): Promise<ProductionLog> {
    return this.api.postJson(`${BASE}/logs`, { lineId, day });
  }
  saveLog(id: string, notes: string | null, rows: LogRowRequest[]): Promise<ProductionLog> {
    return this.api.putJson(`${BASE}/logs/${id}`, { notes, rows });
  }
  submitLog(id: string): Promise<ProductionLog> {
    return this.api.postJson(`${BASE}/logs/${id}/submit`, {});
  }
  returnLog(id: string, reason: string): Promise<ProductionLog> {
    return this.api.postJson(`${BASE}/logs/${id}/return`, { reason });
  }
  approveLog(id: string): Promise<ProductionLog> {
    return this.api.postJson(`${BASE}/logs/${id}/approve`, {});
  }
  book(id: string, rowId: string, employeeId: string, hours: number, overrideReason: string | null, overtimeHours = 0): Promise<ProductionLog> {
    return this.api.postJson(`${BASE}/logs/${id}/rows/${rowId}/bookings`, { employeeId, hours, overrideReason, overtimeHours });
  }
  unbook(id: string, bookingId: string): Promise<ProductionLog> {
    return this.api.deleteJson(`${BASE}/logs/${id}/bookings/${bookingId}`);
  }
  candidates(id: string): Promise<LogEmployeeCandidate[]> {
    return this.api.getJson(`${BASE}/logs/${id}/employee-candidates`);
  }

  // ---------- reports ----------
  posExceptions(filter: { from?: string | null; to?: string | null; branchId?: string | null; productId?: string | null; sale?: string | null; stale?: StaleWord | null }): Promise<PosExceptionReport> {
    return this.api.getJson(`${BASE}/reports/pos-exceptions${query(filter)}`);
  }
  pendingMovements(lineId?: string | null): Promise<PendingMovements> {
    return this.api.getJson(`${BASE}/reports/pending-movements${query({ lineId })}`);
  }
  materialFlow(): Promise<MaterialFlowReport> {
    return this.api.getJson(`${BASE}/reports/material-flow`);
  }

  // ---------- costing (Phase A, read-only) ----------
  costing(id: string): Promise<ProductionCostSheet> {
    return this.api.getJson(`${BASE}/orders/${id}/costing`);
  }
  workInProgress(lineId?: string | null): Promise<WorkInProgressReport> {
    return this.api.getJson(`${BASE}/reports/work-in-progress${query({ lineId })}`);
  }
  /** No year or month: the company's month today, as the server's clock says. */
  labourAbsorption(year: number | null, month: number | null, lineId?: string | null): Promise<LabourAbsorptionReport> {
    return this.api.getJson(`${BASE}/reports/labour-absorption${query({ year, month, lineId })}`);
  }

  // ---------- costing B2 (read only) ----------
  costReconciliation(id: string, asOf: string | null, live: boolean): Promise<OrderCostReconciliation> {
    return this.api.getJson(`${BASE}/orders/${id}/cost-reconciliation${query({ asOf, live: live || null })}`);
  }
  costHistory(filter: CostReportFilter): Promise<CostHistoryReport> {
    return this.api.getJson(`${BASE}/reports/cost-history${query({ ...filter })}`);
  }
  lateCosts(filter: CostReportFilter): Promise<LateCostReport> {
    return this.api.getJson(`${BASE}/reports/late-costs${query({ ...filter })}`);
  }
  costVariances(filter: CostReportFilter): Promise<CostVarianceReport> {
    return this.api.getJson(`${BASE}/reports/cost-variances${query({ ...filter })}`);
  }
  releaseReconciliation(filter: CostReportFilter): Promise<ReleaseReconciliationReport> {
    return this.api.getJson(`${BASE}/reports/release-reconciliation${query({ ...filter })}`);
  }
  scrapCosts(filter: CostReportFilter): Promise<ScrapCostReport> {
    return this.api.getJson(`${BASE}/reports/scrap-costs${query({ ...filter })}`);
  }
  costGaps(filter: CostReportFilter): Promise<CostGapReport> {
    return this.api.getJson(`${BASE}/reports/cost-gaps${query({ ...filter })}`);
  }
  /** The register as a workbook: the same route, so the same permission, line scope and redaction. */
  exportCostRegister(register: CostRegister, filter: CostReportFilter, lang: string): Promise<Blob> {
    const { page: _page, pageSize: _size, ...rest } = filter;
    return this.api.getBlob(`${BASE}/reports/${register}${query({ ...rest, format: 'xlsx', lang })}`, 120_000);
  }
  exportOrderCost(id: string, asOf: string | null, live: boolean, lang: string): Promise<Blob> {
    return this.api.getBlob(`${BASE}/orders/${id}/cost-reconciliation${query({ asOf, live: live || null, format: 'xlsx', lang })}`, 120_000);
  }
}
