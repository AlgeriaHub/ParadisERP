import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ApiError } from '../../../core/api.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { KeepAdvanceDialogComponent, keepable, keepingAllowed } from './keep-advance-dialog.component';
import { ReceiptVoucher, ReceivableSettings, ReceivablesApi } from './receivables-api.service';

describe('KeepAdvanceDialogComponent (SAL-13)', () => {
  let fixture: ComponentFixture<KeepAdvanceDialogComponent>;
  let api: jasmine.SpyObj<ReceivablesApi>;
  let confirm: { ask: jasmine.Spy };

  const advance = {
    id: 'rcv-1', number: 'RCV-0001', customerId: 'c1', status: 'Confirmed', kind: 'Advance', amount: 100_000,
    allocated: 0, unallocated: 100_000, advanceOpen: 70_000, advanceTaxAmount: 0, allocations: [],
  } as unknown as ReceiptVoucher;
  const payment = { ...advance, id: 'rcv-2', number: 'RCV-0002', kind: 'Payment', unallocated: 57_200, advanceOpen: 0 } as ReceiptVoucher;

  async function open(receipt: ReceiptVoucher, confirmed = true): Promise<HTMLElement> {
    api = jasmine.createSpyObj<ReceivablesApi>('ReceivablesApi', ['keepAdvance']);
    api.keepAdvance.and.callFake(async (_id, r) => ({ ...receipt, kept: r.amount ?? 0 }));
    confirm = { ask: jasmine.createSpy('ask').and.resolveTo(confirmed) };
    TestBed.configureTestingModule({
      imports: [KeepAdvanceDialogComponent],
      providers: [
        { provide: ReceivablesApi, useValue: api },
        { provide: ConfirmService, useValue: confirm },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(KeepAdvanceDialogComponent);
    fixture.componentRef.setInput('incomeAccount', '7585 — Autres produits');
    fixture.componentRef.setInput('receipt', receipt);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params);
  const amountBox = (page: HTMLElement) => page.querySelector('#keep-amount') as HTMLInputElement;
  const reasonBox = (page: HTMLElement) => page.querySelector('#keep-reason') as HTMLTextAreaElement;
  const keepButton = (page: HTMLElement) => page.querySelector('[dialogFoot] .btn-primary') as HTMLButtonElement;

  async function type(box: HTMLInputElement | HTMLTextAreaElement, value: string): Promise<void> {
    box.value = value;
    box.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function press(page: HTMLElement): Promise<void> {
    keepButton(page).click();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('offers only money that has arrived and still holds something, and only while the setting allows it', () => {
    expect(keepable(advance)).toBe(70_000);
    expect(keepable(payment)).toBe(57_200);
    expect(keepable({ ...advance, status: 'Pending' } as ReceiptVoucher)).toBe(0);
    expect(keepable({ ...payment, kind: 'Repair' } as ReceiptVoucher)).toBe(0);
    expect(keepingAllowed(null)).toBeFalse();
    expect(keepingAllowed({ keptAdvances: 'NotAllowed', keptIncomeAccount: '7585' } as ReceivableSettings)).toBeFalse();
    expect(keepingAllowed({ keptAdvances: 'OtherIncome', keptIncomeAccount: null } as ReceivableSettings)).toBeFalse();
    expect(keepingAllowed({ keptAdvances: 'OtherIncome', keptIncomeAccount: '7585' } as ReceivableSettings)).toBeTrue();
  });

  it('opens on all the advance still holds, names the income account and the receipt', async () => {
    const page = await open(advance);
    expect(+amountBox(page).value).toBe(70_000);
    expect(page.textContent).toContain('RCV-0001');
    expect(page.textContent).toContain('7585 — Autres produits');
    expect(page.textContent).toContain(t('adv.keep.holds419'));
  });

  it("opens a payment's keep on its unallocated part, from the customer's account", async () => {
    const page = await open(payment);
    expect(+amountBox(page).value).toBe(57_200);
    expect(page.textContent).toContain(t('adv.keep.holds411'));
  });

  it('asks for a reason and refuses more than the receipt holds, sending nothing', async () => {
    const page = await open(advance);
    await press(page);
    expect(page.textContent).toContain(t('adv.keep.reasonRequired'));
    expect(reasonBox(page).getAttribute('aria-invalid')).toBe('true');

    await type(reasonBox(page), 'Commande annulée');
    await type(amountBox(page), '70001');
    await press(page);
    expect(amountBox(page).getAttribute('aria-invalid')).toBe('true');
    expect(confirm.ask).not.toHaveBeenCalled();
    expect(api.keepAdvance).not.toHaveBeenCalled();
  });

  it('asks before acting, then keeps the amount with its reason and hands the receipt back', async () => {
    const page = await open(advance);
    const kept = jasmine.createSpy('kept');
    fixture.componentInstance.kept.subscribe(kept);
    await type(amountBox(page), '30000');
    await type(reasonBox(page), '  Commande annulée  ');
    await press(page);
    expect(confirm.ask).toHaveBeenCalledTimes(1);
    expect(api.keepAdvance).toHaveBeenCalledWith('rcv-1', { amount: 30_000, reason: 'Commande annulée' });
    expect(kept).toHaveBeenCalledWith(jasmine.objectContaining({ id: 'rcv-1', kept: 30_000 }));
  });

  it('does nothing when the confirmation is declined', async () => {
    const page = await open(advance, false);
    await type(reasonBox(page), 'Commande annulée');
    await press(page);
    expect(confirm.ask).toHaveBeenCalled();
    expect(api.keepAdvance).not.toHaveBeenCalled();
  });

  it("words the server's refusal in the reader's language", async () => {
    const page = await open(advance);
    api.keepAdvance.and.rejectWith(new ApiError(JSON.stringify({ detail: 'x', code: 'kept_advances_not_allowed' }), 400));
    await type(reasonBox(page), 'Commande annulée');
    await press(page);
    expect(page.textContent).toContain(t('adv.keep.error.notAllowed'));
  });
});
