import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminCustomer, AdminService, OpeningBalanceRequest, OpeningCorrectionRequest } from '../../core/admin.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import {
  OpeningParty,
  canSetOpeningBalance,
  canSetSupplierOpeningBalance,
  emptyOpeningDraft,
  openingDraftProblem,
  openingPayload,
} from '../../core/opening-balance';
import { ConfirmService } from '../../ui/confirm.service';
import { CustomerOpeningBalanceComponent, OpeningPartyCorrection } from './customer-opening-balance.component';
import { SalesPagesApi } from './sales-pages.api';
import { SuppliersApiService } from './suppliers/suppliers-api.service';

/**
 * A customer's opening balance (owner, 2026-10-10, go-live): typed while registering, set once
 * later only by a user the server lets, then fixed — a correction with its reason is the only
 * change. The screen reads the server's capability, never a role name.
 */
describe('opening balance helpers', () => {
  const today = '2026-10-10';

  it('sends nothing for blank or zero, and the amount with its direction otherwise', () => {
    expect(openingPayload(emptyOpeningDraft(today))).toBeNull();
    expect(openingPayload({ amount: 0, direction: 'owesUs', on: today })).toBeNull();
    expect(openingPayload({ amount: 1500, direction: 'weOwe', on: '2026-10-01' }))
      .toEqual({ amount: 1500, direction: 'weOwe', on: '2026-10-01' });
  });

  it('refuses a negative amount and a future date, and a missing amount only where one is required', () => {
    expect(openingDraftProblem({ amount: -1, direction: 'owesUs', on: today }, today)).toBe('cust.opening.amountPositive');
    expect(openingDraftProblem({ amount: 1, direction: 'owesUs', on: '2026-10-11' }, today)).toBe('cust.opening.noFuture');
    expect(openingDraftProblem(emptyOpeningDraft(today), today)).toBeNull();
    expect(openingDraftProblem(emptyOpeningDraft(today), today, true)).toBe('cust.opening.amountRequired');
  });

  it('reads the capability from the login; a session without capabilities is not allowed', () => {
    expect(canSetOpeningBalance({ capabilities: ['customers.opening_balance'] })).toBeTrue();
    expect(canSetOpeningBalance({ capabilities: ['customers.create'] })).toBeFalse();
    expect(canSetOpeningBalance({ capabilities: undefined })).toBeFalse();
    expect(canSetOpeningBalance(null)).toBeFalse();
    expect(canSetSupplierOpeningBalance({ capabilities: ['suppliers.opening_balance'] })).toBeTrue();
    expect(canSetSupplierOpeningBalance({ capabilities: ['customers.opening_balance'] })).toBeFalse();
    expect(canSetSupplierOpeningBalance(null)).toBeFalse();
  });
});

describe('CustomerOpeningBalanceComponent', () => {
  let fixture: ComponentFixture<CustomerOpeningBalanceComponent>;
  let setCalls: OpeningBalanceRequest[];
  let corrections: OpeningCorrectionRequest[];
  let supplierSetCalls: OpeningBalanceRequest[];
  let supplierCorrections: OpeningCorrectionRequest[];
  /** The corrections the server answers with, a page at a time, and the parties they were asked for. */
  let stored: OpeningPartyCorrection[];
  let askedFor: string[];
  const user = signal<{ capabilities: string[] } | null>(null);

  const row = (over: Partial<AdminCustomer> = {}): AdminCustomer => ({
    id: 'c1', code: 'Cust-1', fullName: 'Karim Test', phone: null, address: null, taxId: null, email: null,
    birthDate: null, creditLimit: 0, balance: 0, loyaltyPoints: 0, version: 1, kind: 'Person',
    idDocumentNumber: null, representative: null, ...over,
  });

  function create(party: OpeningParty | null, capabilities: string[], kind: 'customer' | 'supplier' = 'customer'): void {
    user.set({ capabilities });
    fixture = TestBed.createComponent(CustomerOpeningBalanceComponent);
    fixture.componentRef.setInput('party', party);
    fixture.componentRef.setInput('kind', kind);
    fixture.detectChanges();
  }

  beforeEach(() => {
    setCalls = [];
    corrections = [];
    supplierSetCalls = [];
    supplierCorrections = [];
    stored = [];
    askedFor = [];
    const page = (who: string) => (id: string, skip: number, take: number) => {
      askedFor.push(`${who}:${id}`);
      return Promise.resolve({ items: stored.slice(skip, skip + take), total: stored.length, skip, take });
    };
    TestBed.configureTestingModule({
      imports: [CustomerOpeningBalanceComponent],
      providers: [
        {
          provide: AdminService,
          useValue: {
            setOpeningBalance: (_id: string, r: OpeningBalanceRequest) =>
              (setCalls.push(r), Promise.resolve(row({ openingBalance: r.amount, openingBalanceSetAtUtc: '2026-10-10T10:00:00Z' }))),
            correctOpeningBalance: (_id: string, r: OpeningCorrectionRequest) => (corrections.push(r), Promise.resolve({})),
          },
        },
        {
          provide: SalesPagesApi,
          useValue: { customerOpeningCorrectionsPage: page('customer'), supplierOpeningCorrectionsPage: page('supplier') },
        },
        {
          provide: SuppliersApiService,
          useValue: {
            setOpeningBalance: (_id: string, r: OpeningBalanceRequest) =>
              (supplierSetCalls.push(r), Promise.resolve({
                id: 's1', code: 'Sup-1', name: 'Test Sup', balance: r.amount, openingBalance: r.amount,
                openingBalanceSetAtUtc: '2026-10-10T10:00:00Z', openingBalanceOn: r.on, openingBalanceSetBy: 'admin',
                phone: null, address: null, fiscalIds: {}, note: null,
              })),
            correctOpeningBalance: (_id: string, r: OpeningCorrectionRequest) => (supplierCorrections.push(r), Promise.resolve({})),
          },
        },
        { provide: AuthService, useValue: { currentUser: user } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  const el = () => fixture.nativeElement as HTMLElement;
  const t = (key: string) => TestBed.inject(I18nService).t(key);

  it('offers the three inputs while registering and writes them into the draft', () => {
    create(null, []);
    const amount = el().querySelector('#ob-amount') as HTMLInputElement;
    expect(amount).not.toBeNull();
    expect(el().querySelector('#ob-direction')).not.toBeNull();
    expect(el().querySelector('#ob-on')).not.toBeNull();

    amount.value = '2500';
    amount.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(fixture.componentInstance.draft().amount).toBe(2500);
  });

  it('says "not entered" and offers nothing to a user without the capability', () => {
    create(row(), ['customers.create']);
    expect(el().textContent).toContain(t('cust.opening.state.notSet'));
    expect(el().textContent).toContain(t('cust.opening.adminOnly'));
    expect(el().querySelector('#obs-amount')).toBeNull();
  });

  it('lets a user with the capability set it once, after confirming, and reports the answer', async () => {
    create(row(), ['customers.opening_balance']);
    let reported: OpeningParty | null | undefined;
    fixture.componentInstance.changed.subscribe((c) => (reported = c));

    const amount = el().querySelector('#obs-amount') as HTMLInputElement;
    amount.value = '7500';
    amount.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el().querySelector('.ob-actions .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(setCalls).toEqual([{ amount: 7500, direction: 'owesUs', on: jasmine.any(String) as unknown as string }]);
    expect(reported?.openingBalance).toBe(7500);
  });

  it('shows a set balance as fixed in words, and a correction needs its reason', async () => {
    create(row({ openingBalance: -1200, openingBalanceOn: '2026-10-01', openingBalanceSetAtUtc: '2026-10-01T08:00:00Z', openingBalanceSetBy: 'admin' }),
      ['customers.opening_balance']);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el().textContent).toContain(t('cust.opening.state.set'));
    expect(el().textContent).toContain(t('cust.opening.weOwe'));

    (el().querySelector('.btn-ghost') as HTMLButtonElement).click();
    fixture.detectChanges();
    const amount = el().querySelector('#obc-amount') as HTMLInputElement;
    amount.value = '200';
    amount.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el().querySelector('.ob-actions .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(corrections).toEqual([]);

    const reason = el().querySelector('#obc-reason') as HTMLTextAreaElement;
    reason.value = 'Erreur de saisie';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el().querySelector('.ob-actions .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(corrections.length).toBe(1);
    expect(corrections[0].reason).toBe('Erreur de saisie');
    expect(corrections[0].amount).toBe(200);
  });

  it('reads a set balance\'s corrections a page at a time from the server, the customer\'s or the supplier\'s', async () => {
    stored = Array.from({ length: 12 }, (_, i) => ({
      id: `k${i}`, number: `OBC-${i + 1}`, amount: 100, effectiveOn: '2026-10-02', reason: 'Erreur', createdBy: 'admin',
      createdAtUtc: '2026-10-02T08:00:00Z',
    }));
    create(row({ openingBalance: 1000, openingBalanceOn: '2026-10-01', openingBalanceSetAtUtc: '2026-10-01T08:00:00Z' }), []);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(askedFor).toEqual(['customer:c1']);
    expect(fixture.componentInstance.correctionsPager.total()).toBe(12);
    expect(el().querySelectorAll('tbody tr').length).toBe(10);

    // Nothing is asked while the balance is not set.
    askedFor = [];
    create(row(), []);
    await fixture.whenStable();
    expect(askedFor).toEqual([]);

    create({ id: 's1', openingBalance: 5000, openingBalanceOn: '2026-10-01', openingBalanceSetAtUtc: '2026-10-01T08:00:00Z' }, [], 'supplier');
    await fixture.whenStable();
    expect(askedFor).toEqual(['supplier:s1']);
  });

  it('handles suppliers: party with openingBalance = 5000 shows company owes wording, and setting later calls SuppliersApiService', async () => {
    create(
      { id: 's1', openingBalance: 5000, openingBalanceOn: '2026-10-01', openingBalanceSetAtUtc: '2026-10-01T08:00:00Z', openingBalanceSetBy: 'admin' },
      ['suppliers.opening_balance'],
      'supplier',
    );
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el().textContent).toContain(t('cust.opening.state.set'));
    expect(el().textContent).toContain(t('sup.opening.weOwe'));

    create({ id: 's1' }, ['suppliers.opening_balance'], 'supplier');
    let reported: OpeningParty | null | undefined;
    fixture.componentInstance.changed.subscribe((p) => (reported = p));

    const amount = el().querySelector('#obs-amount') as HTMLInputElement;
    amount.value = '5000';
    amount.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el().querySelector('.ob-actions .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(supplierSetCalls).toEqual([{ amount: 5000, direction: 'owesUs', on: jasmine.any(String) as unknown as string }]);
    expect(reported?.openingBalance).toBe(5000);
  });
});
