/**
 * One mapping from a status to its tone (REDESIGN_PLAN.md §6.5), so no page picks its own badge
 * colour. Keyed by family because the same word means different things: an *Open* invoice is
 * money still owed, an *Open* contract is simply live.
 *
 * Only statuses the code actually has are listed — the domain enums and the client's own outbox
 * states. A status missing here renders neutral, and `status-tones.spec.ts` fails, so a new
 * status cannot quietly get a page-local colour.
 */
export type StatusTone = 'neutral' | 'info' | 'success' | 'warning' | 'critical';

export const STATUS_TONES = {
  /**
   * A contract as the screens state it (`ContractState`, rentals/rental-view.ts): not yet out,
   * out, partly back, all back and still owed, closed — the server's Open/Closed read together
   * with what has left and come back.
   */
  contract: { draft: 'neutral', out: 'info', partly: 'info', unpaid: 'warning', closed: 'success', cancelled: 'neutral' },
  /** `PaymentVoucherStatus`. */
  voucher: { Pending: 'warning', Approved: 'info', Paid: 'success', Cancelled: 'neutral' },
  /** `PaymentStatus` — a cheque or transfer behind a receipt or a till sale. */
  payment: { Pending: 'warning', Confirmed: 'success', Bounced: 'critical' },
  /** `DueWord` (receivables/receivable-view.ts) — how far an uncleared cheque is from its date. */
  due: { overdue: 'critical', today: 'warning', soon: 'warning', later: 'neutral', none: 'neutral' },
  /** `InvoiceStatus` (receivables-api.service.ts). */
  invoice: { Open: 'warning', PartlyPaid: 'info', Settled: 'success', Credited: 'neutral' },
  /** `PayrollRunStatus`. */
  payrollRun: { Draft: 'neutral', Locked: 'info', Approved: 'info', Paid: 'success', Reversed: 'neutral' },
  /** `WageAdvanceStatus`. */
  advance: { Open: 'warning', Recovered: 'success', WrittenOff: 'neutral' },
  /** `OutboxState` — a till sale on its way to the server. */
  outbox: { pending: 'warning', sending: 'info', synced: 'success', rejected: 'critical' },
  /** `SaleStatus`. */
  sale: { Completed: 'success', Refunded: 'neutral' },
  /**
   * What became of one row of the fingerprint terminal's export (FR-3B): a punch of an employee of
   * this branch, a number nobody carries, an employee of another branch, a row that cannot be read.
   */
  punch: { matched: 'success', unknown: 'warning', otherBranch: 'neutral', invalid: 'critical' },
  /**
   * What a punched day says against the working day (owner, 2026-10-10): late, gone early, no
   * check-out — something to put right; time after the end or on the rest day — possible overtime
   * for the supervisor to confirm.
   */
  attendance: { late: 'warning', earlyLeave: 'warning', missingOut: 'warning', possibleOvertime: 'info', restDayWork: 'info' },
  /** Where a till sits: on a warehouse, which does not sell (the owner, 2026-10-05). */
  till: { onWarehouse: 'warning' },
  /**
   * A location's place in the company (the owner, 2026-10-05): the primary branch or the primary
   * warehouse — one of each, always — and the ordinary branches and warehouses beside them.
   */
  location: { primary: 'info', ordinary: 'neutral' },
  /** A customer's opening balance (owner, 2026-10-10): written once and fixed, or still empty. */
  openingBalance: { set: 'info', notSet: 'neutral' },
  /**
   * An employee on the register: still working here, suspended (inactive, not gone), or gone on a
   * day — a person who left keeps every payslip, so "left" is a fact, not an alarm.
   */
  employee: { here: 'success', suspended: 'warning', left: 'neutral' },
  /** An active flag on a product, a customer, a user, a branch… */
  active: { Active: 'success', Inactive: 'neutral' },
  /** `StockTransferStatus`: in transit, partly settled, closed, called off. */
  transfer: { Dispatched: 'warning', PartlyReceived: 'info', Received: 'success', Cancelled: 'neutral' },
  /**
   * Beside a transfer's status (owner, 2026-10-13): the destination reported goods short and the
   * dispatching branch has not yet confirmed or rejected it — goods possibly lost, so it stands out
   * from the warning of "in transit".
   */
  transferShortfall: { reported: 'critical' },
  /**
   * `BomStatus` (manufacturing, Phase 1): a recipe being written, the one in use, or one replaced or
   * withdrawn — kept, frozen, because what was made from it names it.
   */
  bom: { Draft: 'warning', Active: 'success', Retired: 'neutral' },
  /** An employee's assignment to a production line: still working there, or ended on a day. */
  lineAssignment: { working: 'success', ended: 'neutral' },
  /** `ProductionOrderStatus` (manufacturing Phase 2). */
  productionOrder: { Open: 'info', OnHold: 'warning', CompletionRequested: 'warning', Completed: 'success', Cancelled: 'neutral' },
  /**
   * `MaintenanceJobStatus` (maintenance Phase M1): a draft a return note opened (MS-03) waiting to be taken, an open job
   * holding its units, completed (units back to available or to scrap), invoiced (a customer job, M5), or cancelled.
   */
  maintenanceJob: { Draft: 'warning', Open: 'info', Completed: 'success', Invoiced: 'success', Cancelled: 'neutral' },
  /**
   * A row of MS-04's workshop list: the person may be booked on the branch's jobs, has moved to another branch (the row no
   * longer counts there), or has left.
   */
  workshopMember: { bookable: 'success', moved: 'warning', left: 'neutral' },
  /** `ProductionLogStatus`: the daily production record. */
  productionLog: { Draft: 'neutral', Submitted: 'warning', Approved: 'success' },
  /**
   * A till sale of a material not for sale (ManufacturingReports): made before the product was withdrawn
   * (stale offline catalogue), after it, or with no withdrawal on file.
   */
  posException: { stale: 'warning', after_withdrawal: 'critical', unknown: 'neutral' },
  /** One step of closing the books (CLO-01): a month closed, or the books reopened with a reason. */
  bookClosing: { closed: 'success', reopened: 'warning' },
  /** A product sold with no cost (SAL-02): its cost is still 0, or has been entered since. */
  productCost: { missing: 'critical', set: 'info' },
  /** A manufacturing cost sheet (costing, Phase A): approved only when every input is. */
  costSheet: { provisional: 'warning', approved: 'success' },
  /**
   * What one element of a cost sheet rests on (`ProductionCostEngine.Element*`): settled, provisional (a
   * reference cost, a standard rate), incomplete (part has no value), or undecided and so not a figure at all.
   */
  costElement: {
    approved: 'success', recorded: 'success', none: 'neutral', reference_cost: 'warning', provisional: 'warning',
    incomplete: 'critical', not_set: 'neutral', not_recorded: 'neutral',
    // Maintenance M4: an element MS-10 leaves out of a job's cost — final, not a figure waiting for anything.
    excluded: 'neutral',
  },
  /**
   * Costing B2: an order's cost history. A completed order with no snapshot is missing, never a zero cost; one
   * frozen after the chosen day was not frozen then; an open order has none to show.
   */
  costHistory: { frozen: 'info', not_frozen: 'critical', not_frozen_at_date: 'neutral', not_completed: 'neutral' },
  /** How a sheet was made: at the time, or reconstructed afterwards (never presented as a freeze of the time). */
  costSnapshotKind: { created: 'info', reconstructed: 'warning' },
  /** Whether a sheet's sources were captured when it was made (B1 rows made before capture are legacy). */
  costSource: { complete: 'success', incompletelegacy: 'critical' },
  /** An integrity diagnostic. Nothing is ever repaired. */
  costCheck: { pass: 'success', fail: 'critical', not_applicable: 'neutral' },
  /** What a late cost does to the provisional cost: counted, kept apart for want of an element, or doubted. */
  lateTreatment: { recognized: 'info', unclassified: 'warning', uncertain: 'warning' },
  /** How much a cost gap matters. */
  costGap: { missing: 'critical', integrity: 'critical', incomplete: 'warning', provisional: 'warning', review: 'warning', info: 'neutral' },
  /**
   * A year of a fixed-asset schedule (declining balance, 2026-11-10): the year a declining-balance layer turns to straight
   * line, a year computed from the months ended, a year projected.
   */
  depreciationYear: { switch: 'info', computed: 'neutral', projected: 'neutral' },
  /** An account's two-step login (owner, 2026-11-11): on is the safer state; off is a fact, not an alarm. */
  twoFactor: { on: 'success', off: 'neutral' },
} as const satisfies Record<string, Record<string, StatusTone>>;

export type StatusFamily = keyof typeof STATUS_TONES;

export function toneOf(family: StatusFamily, status: string | null | undefined): StatusTone {
  const tones = STATUS_TONES[family] as Record<string, StatusTone>;
  return (status && tones[status]) || 'neutral';
}
