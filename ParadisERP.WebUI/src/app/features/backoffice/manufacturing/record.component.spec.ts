import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { MfgRecordComponent } from './record.component';
import { ProductionApiService, ProductionLog } from './production-api.service';

describe('MfgRecordComponent', () => {
  let fixture: ComponentFixture<MfgRecordComponent>;
  let production: jasmine.SpyObj<ProductionApiService>;

  const log = (over: Partial<ProductionLog> = {}): ProductionLog => ({
    log: { id: 'g1', number: 'PL-000001', lineId: 'l1', lineCode: 'LA', day: '2026-10-20', status: 'Draft', rowCount: 1, hours: 5, submittedBy: null, approvedBy: null },
    notes: null, returnedReason: null, createdBy: 'admin', submittedAtUtc: null, approvedAtUtc: null,
    rows: [{
      id: 'r1', sequence: 1, orderId: 'o1', orderNumber: 'MO-000001', stageId: 's2', stageNameAr: 'التشكيل', stageNameFr: 'Frappe', stageNameEn: 'Form',
      isOutputStage: true, category: null, productId: 'p1', productSku: 'NAIL-70', baseUnitCode: 'kg', specification: 'Ø 3 mm',
      plannedQty: 100, completedQty: 70, rejectedQty: 0, rejectReason: null, notes: null,
      bookings: [{ id: 'b1', employeeId: 'e1', employeeNumber: 'E-001', employeeName: 'Karim Benali', hours: 5, attendanceHours: 8, overrideReason: null, overriddenBy: null, userName: 'admin', atUtc: '2026-10-20T10:00:00Z', overtimeHours: 2 }],
      consumptions: [],
    }],
    warnings: [{ code: 'stage_ahead_of_previous', orderNumber: 'MO-000001', stageNameAr: 'التشكيل', stageNameFr: 'Frappe', stageNameEn: 'Form',
      previousNameAr: 'القص', previousNameFr: 'Coupe', previousNameEn: 'Cut', stageGood: 70, previousGood: 60 }],
    canEdit: true, canApprove: false,
    ...over,
  });

  async function render(l: ProductionLog, capabilities: string[] = []): Promise<void> {
    production = jasmine.createSpyObj<ProductionApiService>('ProductionApiService', ['log', 'orders', 'order', 'candidates', 'book']);
    production.log.and.resolveTo(l);
    production.orders.and.resolveTo([]);
    production.order.and.resolveTo({ stages: [] } as never);
    TestBed.configureTestingModule({
      imports: [MfgRecordComponent],
      providers: [
        { provide: ProductionApiService, useValue: production },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
        { provide: AuthService, useValue: { currentUser: () => ({ capabilities }) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(MfgRecordComponent);
    fixture.componentRef.setInput('id', 'g1');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('says a stage ahead of the one before in the reader\'s words, and shows each person\'s hours', async () => {
    await render(log());
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('MO-000001');
    expect(text).toContain('Frappe');
    expect(text).toContain('Coupe');
    expect(text).toContain('Karim Benali');
    expect(text).not.toContain('stage_ahead_of_previous');
  });

  it('asks for an override reason only from a reader who holds the override, and only past the day\'s attendance', async () => {
    await render(log(), ['manufacturing.labour.override']);
    const c = fixture.componentInstance;
    c.candidates.set([
      { employeeId: 'e1', number: 'E-001', fullName: 'Karim Benali', attendanceHours: 8, attendanceSource: 'recorded', bookedHours: 5, payrollLocked: false },
      { employeeId: 'e2', number: 'E-002', fullName: 'Samir Haddad', attendanceHours: null, attendanceSource: 'declared', bookedHours: 0, payrollLocked: false },
    ]);
    c.bookEmployee = 'e1';
    c.bookHours = 3;
    expect(c.overNeeded()).toBeFalse();
    c.bookHours = 4;
    expect(c.overNeeded()).toBeTrue();
    c.bookEmployee = 'e2';
    c.bookHours = 1;
    // Declared per month: no day to read, so any hours need the override.
    expect(c.overNeeded()).toBeTrue();
    expect(c.canOverride()).toBeTrue();
  });

  it("says how many of each person's hours were overtime and sends the overtime hours with a booking (C-05)", async () => {
    await render(log());
    const c = fixture.componentInstance;
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain(c.i18n.t('mfg.records.overtimeOf', { n: '2' }));

    production.book.and.resolveTo(log());
    c.candidates.set([
      { employeeId: 'e2', number: 'E-002', fullName: 'Samir Haddad', attendanceHours: 8, attendanceSource: 'recorded', bookedHours: 0, payrollLocked: false },
    ]);
    c.bookingRow.set(log().rows[0]);
    c.bookEmployee = 'e2';
    c.bookHours = 3;
    c.bookOvertime = 4;
    expect(c.overtimeInvalid()).toBeTrue();
    c.bookOvertime = 1.5;
    expect(c.overtimeInvalid()).toBeFalse();
    await c.book();
    expect(production.book).toHaveBeenCalledWith('g1', 'r1', 'e2', 3, null, 1.5);
  });

  it('keeps a row that holds hours on its operation', async () => {
    await render(log());
    const c = fixture.componentInstance;
    expect(c.used(c.rows[0])).toBeTrue();
  });
});
