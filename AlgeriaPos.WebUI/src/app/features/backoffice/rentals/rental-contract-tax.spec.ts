import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AdminService } from '../../../core/admin.service';
import { CatalogStore } from '../../../core/catalog-store';
import { AuthService } from '../../../core/auth.service';
import { ConnectivityService } from '../../../core/connectivity.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { StoreDocumentsApi } from '../../print/store-documents-api.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { RentalContractFormComponent } from './rental-contract-form.component';
import { RentalSettings, RentalsApi } from './rentals-api.service';

/**
 * A rental contract's tax choice. The owner (2026-11-05): VAT only when the customer asks for a tax invoice, and every
 * seller decides when the contract is made. A tick box, as at the till: a new contract starts unticked, and anyone may
 * tick or untick it, whatever the untaxed-invoice permission says.
 */
describe('rental contract tax choice', () => {
  function open(capabilities: string[], taxRate: number) {
    TestBed.configureTestingModule({
      providers: [
        { provide: RentalsApi, useValue: { listProductMultipliers: async () => [], listSites: async () => [] } },
        { provide: AdminService, useValue: { listRepresentatives: async () => [] } },
        { provide: StoreDocumentsApi, useValue: { get: async () => ({ contractWordings: [] }) } },
        { provide: CatalogStore, useValue: { active: signal([]), all: signal([]), ensure: async () => [], refresh: async () => [] } },
        {
          provide: AuthService,
          useValue: { currentUser: signal({ role: 'Manager', branchId: 'b1', branchLocked: false, permissions: [], capabilities }) },
        },
        { provide: ConnectivityService, useValue: { online: signal(true) } },
        { provide: ConfirmService, useValue: { ask: async () => true } },
      ],
    });
    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
    const fixture = TestBed.createComponent(RentalContractFormComponent);
    fixture.componentRef.setInput('settings', { overdueMultiplier: 1.5, depositEnabled: false, taxRate, cutoffHour: null } as RentalSettings);
    fixture.detectChanges();
    const box = () => fixture.nativeElement.querySelector('#rf-tax') as HTMLInputElement;
    const hint = () => (fixture.nativeElement.querySelector('#rf-tax-hint') as HTMLElement).textContent!.trim();
    return { fixture, form: fixture.componentInstance, box, hint, i18n };
  }

  it('starts without VAT, and a tick puts the hire rate on the whole contract', async () => {
    const { fixture, form, box, hint, i18n } = open([], 19);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(form.taxMode()).toBe('Untaxed');
    expect(box().type).toBe('checkbox');
    expect([box().checked, box().disabled]).toEqual([false, false]);
    expect(hint()).toBe(i18n.t('rent.form.taxHint.untaxed'));

    // The customer asks for a tax invoice: ticked, by a seller without any untaxed permission.
    box().click();
    fixture.detectChanges();
    expect(form.taxMode()).toBe('Taxed');
    const name = i18n.rateLabel(19);
    expect(hint()).toBe(i18n.t('rent.form.taxHint.rate', { rate: name ? `19 % (${name})` : '19 %' }));

    // And unticked again.
    box().click();
    fixture.detectChanges();
    expect(form.taxMode()).toBe('Untaxed');
  });

  it('is never locked, whatever the permission says', async () => {
    for (const capabilities of [[], ['finance.invoices.untaxed']]) {
      TestBed.resetTestingModule();
      const { fixture, box } = open(capabilities, 19);
      await fixture.whenStable();
      fixture.detectChanges();
      expect(box().disabled).toBeFalse();
    }
  });

  it('says a taxed contract carries no VAT while no hire rate is set', async () => {
    const { fixture, form, hint, i18n } = open([], 0);
    form.taxMode.set('Taxed');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(hint()).toBe(i18n.t('rent.form.taxHint.noRate'));
  });
});
