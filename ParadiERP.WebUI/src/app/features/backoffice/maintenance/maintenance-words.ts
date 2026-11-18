import { adminErrorMessage, problemCode } from '../../../core/admin.service';

/**
 * The refusals of the maintenance routes (Phase M1, `MaintenanceJobs`) the screens say in the reader's language; any
 * other falls back to the server's own sentence.
 */
const REFUSALS: Readonly<Record<string, string>> = {
  maintenance_off: 'maint.error.off',
  exceeds_maintenance: 'maint.error.exceedsMaintenance',
  line_covered: 'maint.error.lineCovered',
  own_job: 'maint.error.ownJob',
  maintenance_option_not_yet: 'maint.error.customerLater',
  not_live: 'maint.error.notLive',
  not_open: 'maint.error.notOpen',
  reason_required: 'maint.error.reasonRequired',
  invalid_quantity: 'maint.error.quantities',
  stock_state: 'maint.error.stockState',
  // Phase M2: parts and outside work.
  parts_at_job: 'maint.error.partsAtJob',
  insufficient_stock: 'maint.error.insufficientStock',
  reserved_by_production: 'maint.error.reservedByProduction',
  over_balance: 'maint.error.overBalance',
  not_stocked: 'maint.error.notStocked',
  voucher_amount_exceeded: 'maint.error.voucherExceeded',
  voucher_cancelled: 'maint.error.voucherCancelled',
  voucher_not_expense: 'maint.error.voucherNotExpense',
  invalid_amount: 'maint.error.invalidAmount',
  // Phase M5a: customer jobs, their price and invoice.
  price_below_cost: 'maint.error.priceBelowCost',
  price_required: 'maint.error.priceRequired',
  already_invoiced: 'maint.error.alreadyInvoiced',
  not_completed: 'maint.error.notCompleted',
  not_customer_job: 'maint.error.notCustomerJob',
  equipment_required: 'maint.error.equipmentRequired',
  customer_required: 'maint.error.customerRequired',
  maintenance_view_required: 'maint.error.viewRequired',
  not_priceable: 'maint.error.notPriceable',
  invalid_price: 'maint.error.invalidPrice',
  // MS-34: income on its own account while the account map names no repair service income.
  accounts_not_mapped: 'maint.error.incomeNotMapped',
  // Phase M7c: rework under guarantee (MS-27, MS-37).
  rework_free: 'maint.error.reworkFree',
  rework_not_customer_job: 'maint.error.reworkNotCustomerJob',
  rework_of_rework: 'maint.error.reworkOfRework',
  rework_not_completed: 'maint.error.reworkNotCompleted',
  rework_other_customer: 'maint.error.reworkOtherCustomer',
  outside_guarantee: 'maint.error.outsideGuarantee',
  // Phase M7d (MS-38 "per piece"): a type tracked piece by piece is one named piece per job.
  piece_reference_required: 'maint.error.pieceReferenceRequired',
  one_piece_per_job: 'maint.error.onePiecePerJob',
};

/**
 * MS-34 "parts and labour apart": the server writes a job invoice's two lines as "Pièces — MJ-…" and
 * "Main-d'œuvre et service — MJ-…" (`MaintenanceCustomerJobs.PartsLineWords` / `LabourLineWords`), in French like the
 * other words it puts on a document. The invoice and credit note say those words in the reader's (or the paper's)
 * language; any other line name is shown as stored.
 */
const SPLIT_LINE = /^(Pièces|Main-d['’]œuvre et service) — (MJ-.*)$/;

export function invoiceLineName(name: string, t: (key: string, params?: Record<string, string | number>) => string): string {
  const m = SPLIT_LINE.exec(name ?? '');
  if (!m) return name;
  return t(m[1] === 'Pièces' ? 'print.line.maintenanceParts' : 'print.line.maintenanceLabour', { n: m[2] });
}

export function maintenanceError(error: unknown, t: (key: string) => string): string {
  const code = problemCode(error);
  return code && REFUSALS[code] ? t(REFUSALS[code]) : adminErrorMessage(error);
}
