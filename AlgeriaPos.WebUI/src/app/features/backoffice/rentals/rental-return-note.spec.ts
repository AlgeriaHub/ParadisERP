import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { RentalReturnNoteComponent } from './rental-return-note.component';
import { ContractDocuments, RentalSettings, RentalsApi, ReturnNoteRequest } from './rentals-api.service';

/**
 * The late question on the return note (the owner, 2026-10-08), on the owner's example: one unit
 * for three days at 100 a day (150 a late day), out on the 26th at 15:00 under a noon cutoff, so
 * its period is the 27th to the 29th and it is due back on the 30th before noon.
 */
describe('the return note asks about a late return', () => {
  const docs = {
    contract: {
      id: 'c1',
      number: 'CTR-0001',
      customerId: 'cu1',
      customerName: 'Ben Ali BTP',
      branchId: 'b1',
      lines: [
        {
          id: 'l1', productId: 'p1', sku: 'ECH-01', name: 'Échafaudage', qty: 1, durationDays: 3,
          rentUnitPrice: 100, damageUnitPrice: 2000, overdueUnitPrice: 150, parentLineId: null,
          issuedQty: 1, leftToIssueQty: 0, settledQty: 0, stillOutQty: 1, taxRate: 0,
        },
      ],
    },
    issueNotes: [
      {
        id: 'i1', issuedAtUtc: '2026-09-26T14:00:00Z', periodStartsOn: '2026-09-27',
        lines: [{ contractLineId: 'l1', qty: 1 }],
      },
    ],
    returnNotes: [],
    extensions: [],
    settlement: {},
  } as unknown as ContractDocuments;

  let returned: jasmine.Spy;
  let previewReturn: jasmine.Spy;
  let isLateReturn = false;
  const lateCharged = (request: ReturnNoteRequest) =>
    Math.max(0, 150 - 150 * (request.lateDaysWaived ?? 0) - (request.lateAmountWaived ?? 0));

  beforeEach(() => {
    returned = jasmine.createSpy('return').and.resolveTo({ id: 'r1' });
    previewReturn = jasmine.createSpy('previewReturn').and.callFake(async (_id: string, request: ReturnNoteRequest) => ({
      rows: [{ returnLineIndex: 0, contractLineId: 'l1', issueNoteId: 'i1', issueNumber: 'ISS-1',
        issuedAtUtc: '2026-09-26T14:00:00Z', qty: 1, physicalQty: 1, sameDayUsageRequired: false,
        sameDayUsage: 'NotApplicable' as const, chargeableDays: 3, dailyUnitRate: 100, rentalCharge: 300 }],
      // The server's figures, deduction included: 150 less a day of 150, or less the amount.
      charges: request.chargeOverdue === true && lateCharged(request) > 0 ? [{ id: 'late', contractLineId: 'l1', kind: 'Overdue',
        amount: lateCharged(request), returnNoteId: 'draft', issueNoteId: 'i1', extensionId: null, qty: 1, days: 1,
        unitPrice: lateCharged(request), isPosted: true }] : [],
      chargeTotal: request.chargeOverdue === true ? 300 + lateCharged(request) : 300, unusedRent: 0, customerBalance: 0, previouslyPaid: 0,
      customerCredit: 0, reliefDebtReduction: 0, reliefPaymentVoucher: 0, isClosed: true,
      late: isLateReturn
        ? { dueOn: '2026-09-30', days: request.chargeOverdue === false ? 0 : 1, amount: request.chargeOverdue === false ? 0 : 150, minutes: 180 }
        : null,
      canConfirm: true, problems: [],
    }));
    TestBed.configureTestingModule({
      providers: [
        { provide: RentalsApi, useValue: { readContract: async () => docs, return: returned, previewReturn } },
        {
          provide: AuthService,
          useValue: { currentUser: signal({ role: 'Admin', branchId: 'b1', branchLocked: false, permissions: [], capabilities: [] }) },
        },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  async function backAt(day: string, time: string): Promise<ComponentFixture<RentalReturnNoteComponent>> {
    isLateReturn = time >= '12:00';
    const fixture = TestBed.createComponent(RentalReturnNoteComponent);
    fixture.componentRef.setInput('contractId', 'c1');
    fixture.componentRef.setInput('settings', {
      overdueMultiplier: 1.5, depositEnabled: false, taxRate: 0, dayCutoffHour: 12,
    } as RentalSettings);
    fixture.detectChanges();
    await fixture.whenStable();
    const note = fixture.componentInstance;
    note.returnedOn.set(day);
    note.returnedTime.set(time);
    note.customerTransport.set(true);
    note.patch('l1', { qtyOk: 1 });
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 250));
    fixture.detectChanges();
    return fixture;
  }

  const sent = (): ReturnNoteRequest => returned.calls.mostRecent().args[1];

  it('asks nothing when the goods are back on time, and sends no answer', async () => {
    const fixture = await backAt('2026-09-30', '09:00');
    const note = fixture.componentInstance;

    expect(note.late()).toBeNull();
    expect(fixture.nativeElement.querySelector('#rr-late')).toBeNull();
    expect(note.charges().some((c) => c.kind === 'Overdue')).toBeFalse();

    await note.save();
    expect(sent().chargeOverdue).toBeNull();
    expect(sent().lateWaiverReason).toBeNull();
  });

  it('says how late in words and will not send the note until the question is answered', async () => {
    const fixture = await backAt('2026-09-30', '15:00');
    const note = fixture.componentInstance;

    expect(note.late()).toEqual({ dueOn: '2026-09-30', days: 1, amount: 150, minutes: 180 });
    const block = fixture.nativeElement.querySelector('#rr-late') as HTMLElement;
    expect(block).not.toBeNull();
    expect(block.textContent).toContain(note.i18n.t('rent.late.state'));
    expect(note.lateSentence()).toContain(note.i18n.t('rent.late.inHours', { n: 3 }));
    // Two answers, neither chosen for the clerk.
    expect((block.querySelector('#rr-late-charge') as HTMLInputElement).checked).toBeFalse();
    expect((block.querySelector('#rr-late-waive') as HTMLInputElement).checked).toBeFalse();

    await note.save();
    fixture.detectChanges();
    expect(returned).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain(note.i18n.t('rent.late.answerRequired'));
  });

  it('charged, previews the late day and sends only the answer', async () => {
    const fixture = await backAt('2026-09-30', '15:00');
    const note = fixture.componentInstance;

    (fixture.nativeElement.querySelector('#rr-late-charge') as HTMLInputElement).click();
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 250));
    fixture.detectChanges();
    expect(note.charges().filter((c) => c.kind === 'Overdue').map((c) => c.amount)).toEqual([150]);

    await note.save();
    expect(sent().chargeOverdue).toBeTrue();
    expect(sent().lateWaiverReason).toBeNull();
    // No figure travels: the server prices the late day itself.
    expect(Object.keys(sent()).filter((k) => /amount|total|price/i.test(k))).toEqual([]);
  });

  it('waived, drops the late day from the preview and needs the reason before it sends', async () => {
    const fixture = await backAt('2026-09-30', '15:00');
    const note = fixture.componentInstance;

    (fixture.nativeElement.querySelector('#rr-late-waive') as HTMLInputElement).click();
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 250));
    fixture.detectChanges();
    expect(note.charges().some((c) => c.kind === 'Overdue')).toBeFalse();
    expect(note.chargeTotal()).toBe(300);

    await note.save();
    fixture.detectChanges();
    expect(returned).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain(note.i18n.t('rent.late.reasonRequired'));

    note.lateReason.set('  Camion bloqué au barrage ');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 250));
    expect(note.canSave()).toBeTrue();
    await note.save();
    expect(sent().chargeOverdue).toBeFalse();
    expect(sent().lateWaiverReason).toBe('Camion bloqué au barrage');
  });

  async function settle(fixture: ComponentFixture<RentalReturnNoteComponent>): Promise<void> {
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 250));
    fixture.detectChanges();
  }

  it('with a deduction in days, sends the days and the reason and never a price', async () => {
    const fixture = await backAt('2026-09-30', '15:00');
    const note = fixture.componentInstance;

    (fixture.nativeElement.querySelector('#rr-late-deduct') as HTMLInputElement).click();
    await settle(fixture);
    // Days first, and the bound said in words.
    expect((fixture.nativeElement.querySelector('#rr-late-by-days') as HTMLInputElement).checked).toBeTrue();
    expect(fixture.nativeElement.textContent).toContain(note.i18n.t('rent.late.deductDaysHint', { n: 1 }));

    note.lateDeduction.set(1);
    await settle(fixture);
    expect(note.charges().some((c) => c.kind === 'Overdue')).toBeFalse();

    await note.save();
    fixture.detectChanges();
    expect(returned).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain(note.i18n.t('rent.late.deductReasonRequired'));

    note.lateReason.set(' Client fidèle ');
    await settle(fixture);
    await note.save();
    expect(sent().chargeOverdue).toBeTrue();
    expect(sent().lateDaysWaived).toBe(1);
    expect(sent().lateAmountWaived).toBeNull();
    expect(sent().lateWaiverReason).toBe('Client fidèle');
  });

  it('with a deduction as an amount, never more than the late total, and shows what is charged', async () => {
    const fixture = await backAt('2026-09-30', '15:00');
    const note = fixture.componentInstance;

    (fixture.nativeElement.querySelector('#rr-late-deduct') as HTMLInputElement).click();
    await settle(fixture);
    (fixture.nativeElement.querySelector('#rr-late-by-amount') as HTMLInputElement).click();
    note.lateReason.set('Client fidèle');
    note.lateDeduction.set(200);
    await settle(fixture);

    await note.save();
    fixture.detectChanges();
    expect(returned).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain(
      note.i18n.t('rent.late.deductionAmountInvalid', { v: note.i18n.money(150) }),
    );

    note.lateDeduction.set(50);
    await settle(fixture);
    expect(note.deductionResult()).toEqual({ charged: 100, waived: 50 });
    expect(fixture.nativeElement.textContent).toContain(
      note.i18n.t('rent.late.deductionResult', { charged: note.i18n.money(100), waived: note.i18n.money(50) }),
    );

    await note.save();
    expect(sent().chargeOverdue).toBeTrue();
    expect(sent().lateAmountWaived).toBe(50);
    expect(sent().lateDaysWaived).toBeNull();
  });

  it('with a deduction chosen and left empty, does not send it as a plain yes', async () => {
    const fixture = await backAt('2026-09-30', '15:00');
    const note = fixture.componentInstance;

    (fixture.nativeElement.querySelector('#rr-late-deduct') as HTMLInputElement).click();
    note.lateReason.set('Client fidèle');
    await settle(fixture);

    await note.save();
    fixture.detectChanges();
    expect(returned).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain(note.i18n.t('rent.late.deductionRequired'));
  });

  describe('compact card redesign and live reconciliation', () => {
    it('calculates live reconciliation correctly for complete, partial, and exceeding returns', async () => {
      const fixture = await backAt('2026-09-30', '09:00');
      const note = fixture.componentInstance;
      const line = note.returnable()[0];

      // Initially 1 unit out:
      expect(note.allocatedQty(line.id)).toBe(1);
      expect(note.reconciliationState(line)).toBe('complete');
      expect(note.reconciliationText(line)).toContain('✓');

      // Partial (0 allocated):
      note.patch(line.id, { qtyOk: 0 });
      fixture.detectChanges();
      expect(note.allocatedQty(line.id)).toBe(0);
      expect(note.reconciliationState(line)).toBe('none');

      // Exceeds (2 allocated for 1 out):
      note.patch(line.id, { qtyOk: 2 });
      fixture.detectChanges();
      expect(note.allocatedQty(line.id)).toBe(2);
      expect(note.reconciliationState(line)).toBe('exceeds');
      expect(note.problemOf(line)).toBe('exceedsIssued');
      expect(note.hasCardError(line)).toBeTrue();
    });

    it('sets all returned sound via the convenience action', async () => {
      const fixture = await backAt('2026-09-30', '09:00');
      const note = fixture.componentInstance;
      const line = note.returnable()[0];

      // Reset to 0
      note.patch(line.id, { qtyOk: 0, qtyPartialDamage: 1, maintenanceValue: 50 });
      fixture.detectChanges();
      expect(note.draft(line.id).qtyPartialDamage).toBe(1);

      // Trigger convenience button
      note.setAllSound(line);
      fixture.detectChanges();

      expect(note.draft(line.id).qtyOk).toBe(line.stillOutQty);
      expect(note.draft(line.id).qtyPartialDamage).toBe(0);
      expect(note.draft(line.id).maintenanceValue).toBe(0);
      expect(note.reconciliationState(line)).toBe('complete');
    });

    it('shows repair cost only when repairable > 0 and loss date only when missing > 0', async () => {
      const fixture = await backAt('2026-09-30', '09:00');
      const note = fixture.componentInstance;
      const line = note.returnable()[0];

      // Sound only
      note.patch(line.id, { qtyOk: 1, qtyPartialDamage: 0, qtyMissing: 0 });
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('#rr-mv' + line.id)).toBeNull();
      expect(fixture.nativeElement.querySelector('#rr-rp' + line.id)).toBeNull();

      // Repairable > 0 shows repair cost
      note.patch(line.id, { qtyOk: 0, qtyPartialDamage: 1 });
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('#rr-mv' + line.id)).not.toBeNull();
      expect(fixture.nativeElement.querySelector('#rr-rp' + line.id)).toBeNull();

      // Missing > 0 shows loss report date
      note.patch(line.id, { qtyPartialDamage: 0, qtyMissing: 1 });
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('#rr-mv' + line.id)).toBeNull();
      expect(fixture.nativeElement.querySelector('#rr-rp' + line.id)).not.toBeNull();
    });

    it('computes document summary metrics correctly', async () => {
      const fixture = await backAt('2026-09-30', '09:00');
      const note = fixture.componentInstance;
      const line = note.returnable()[0];

      note.patch(line.id, { qtyOk: 1, qtyPartialDamage: 0, qtyTotalDamage: 0, qtyMissing: 0 });
      fixture.detectChanges();

      expect(note.totalOutBefore()).toBe(1);
      expect(note.totalGood()).toBe(1);
      expect(note.totalRepairable()).toBe(0);
      expect(note.totalReturned()).toBe(1);
      expect(note.totalOutAfter()).toBe(0);
    });

    it('toggles card collapse and expansion', async () => {
      const fixture = await backAt('2026-09-30', '09:00');
      const note = fixture.componentInstance;
      const line = note.returnable()[0];

      expect(note.isCardCollapsed(line.id)).toBeFalse();
      note.toggleCard(line.id);
      expect(note.isCardCollapsed(line.id)).toBeTrue();

      note.toggleCard(line.id);
      expect(note.isCardCollapsed(line.id)).toBeFalse();
    });

    it('automatically expands a card if it has an unresolved validation error', async () => {
      const fixture = await backAt('2026-09-30', '09:00');
      const note = fixture.componentInstance;
      const line = note.returnable()[0];

      // Initially collapsed
      note.collapsedCards.set({ [line.id]: true });
      expect(note.isCardCollapsed(line.id)).toBeTrue();

      // Allocating more than stillOutQty creates an error -> must auto-expand!
      note.patch(line.id, { qtyOk: line.stillOutQty + 1 });
      fixture.detectChanges();
      expect(note.hasCardError(line)).toBeTrue();
      expect(note.isCardCollapsed(line.id)).toBeFalse();

      // Toggling an invalid card does not collapse it
      note.toggleCard(line.id);
      expect(note.isCardCollapsed(line.id)).toBeFalse();
    });
  });
});
