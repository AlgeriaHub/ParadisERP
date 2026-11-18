import { STATUS_TONES, StatusTone, toneOf } from './status-tones';
import { ICON_NAMES } from './icon.component';
import { NAV } from '../core/nav';

describe('status tones', () => {
  const tones: StatusTone[] = ['neutral', 'info', 'success', 'warning', 'critical'];

  it('uses only the five tones', () => {
    for (const [family, map] of Object.entries(STATUS_TONES)) {
      for (const [status, tone] of Object.entries(map)) {
        expect(tones).withContext(`${family}.${status}`).toContain(tone);
      }
    }
  });

  it('covers every status the domain enums and the outbox declare', () => {
    // Spelled out here on purpose: adding a status to an enum without giving it a tone fails this.
    expect(Object.keys(STATUS_TONES.voucher)).toEqual(['Pending', 'Approved', 'Paid', 'Cancelled']);
    expect(Object.keys(STATUS_TONES.contract)).toEqual(['draft', 'out', 'partly', 'unpaid', 'closed', 'cancelled']);
    expect(Object.keys(STATUS_TONES.payment)).toEqual(['Pending', 'Confirmed', 'Bounced']);
    expect(Object.keys(STATUS_TONES.due)).toEqual(['overdue', 'today', 'soon', 'later', 'none']);
    expect(Object.keys(STATUS_TONES.invoice)).toEqual(['Open', 'PartlyPaid', 'Settled', 'Credited']);
    expect(Object.keys(STATUS_TONES.payrollRun)).toEqual(['Draft', 'Locked', 'Approved', 'Paid', 'Reversed']);
    expect(Object.keys(STATUS_TONES.advance)).toEqual(['Open', 'Recovered', 'WrittenOff']);
    expect(Object.keys(STATUS_TONES.outbox)).toEqual(['pending', 'sending', 'synced', 'rejected']);
    expect(Object.keys(STATUS_TONES.punch)).toEqual(['matched', 'unknown', 'otherBranch', 'invalid']);
    expect(Object.keys(STATUS_TONES.attendance)).toEqual(['late', 'earlyLeave', 'missingOut', 'possibleOvertime', 'restDayWork']);
    expect(Object.keys(STATUS_TONES.transfer)).toEqual(['Dispatched', 'PartlyReceived', 'Received', 'Cancelled']);
    expect(Object.keys(STATUS_TONES.transferShortfall)).toEqual(['reported']);
    expect(Object.keys(STATUS_TONES.employee)).toEqual(['here', 'suspended', 'left']);
    expect(Object.keys(STATUS_TONES.twoFactor)).toEqual(['on', 'off']);
    expect(Object.keys(STATUS_TONES.bom)).toEqual(['Draft', 'Active', 'Retired']);
    expect(Object.keys(STATUS_TONES.lineAssignment)).toEqual(['working', 'ended']);
    expect(Object.keys(STATUS_TONES.productionOrder)).toEqual(['Open', 'OnHold', 'CompletionRequested', 'Completed', 'Cancelled']);
    expect(Object.keys(STATUS_TONES.productionLog)).toEqual(['Draft', 'Submitted', 'Approved']);
    // Maintenance M1: every MaintenanceJobStatus, in the server's order (Domain/MaintenanceEntities.cs).
    expect(Object.keys(STATUS_TONES.maintenanceJob)).toEqual(['Draft', 'Open', 'Completed', 'Invoiced', 'Cancelled']);
    // MS-04: a row of the workshop list (MaintenanceWorkshopMemberDto's effective, employeeBranchId and employed).
    expect(Object.keys(STATUS_TONES.workshopMember)).toEqual(['bookable', 'moved', 'left']);
    expect(Object.keys(STATUS_TONES.posException)).toEqual(['stale', 'after_withdrawal', 'unknown']);
    expect(Object.keys(STATUS_TONES.productCost)).toEqual(['missing', 'set']);
    expect(Object.keys(STATUS_TONES.bookClosing)).toEqual(['closed', 'reopened']);
    expect(Object.keys(STATUS_TONES.depreciationYear)).toEqual(['switch', 'computed', 'projected']);
    expect(Object.keys(STATUS_TONES.costSheet)).toEqual(['provisional', 'approved']);
    expect(Object.keys(STATUS_TONES.costElement)).toEqual(
      ['approved', 'recorded', 'none', 'reference_cost', 'provisional', 'incomplete', 'not_set', 'not_recorded', 'excluded']);
  });

  it('gives a bounced instrument and a rejected sale the critical tone, and anything unknown neutral', () => {
    expect(toneOf('payment', 'Bounced')).toBe('critical');
    expect(toneOf('outbox', 'rejected')).toBe('critical');
    expect(toneOf('voucher', 'Nonsense')).toBe('neutral');
    expect(toneOf('voucher', null)).toBe('neutral');
  });
});

describe('module icons (D1)', () => {
  it('has a drawing for every sidebar module', () => {
    for (const m of NAV) expect(ICON_NAMES).withContext(m.key).toContain(m.icon);
  });

  it('stays the narrow exception: the modules of the plan, no more', () => {
    // The dashboard's drawing is ready for Phase 4; nothing else may join the set without a decision.
    // Manufacturing joined with its module (Phase 1, 2026-10-25); maintenance with its (plan §7, Phase M0, 2026-11-09).
    expect(ICON_NAMES.length).toBe(12);
  });
});
