import { TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { RentalSettingsComponent } from './rental-settings.component';
import { RentalPagesApi } from './rental-pages.api';
import { RentalSettings, RentalsApi } from './rentals-api.service';

/**
 * Verifies that rental settings can be saved even when the stored overdue multiplier (no longer
 * displayed or editable on this page) is outside the 1..10 range, while visible inputs such as
 * the tax rate continue to prevent saving when invalid.
 */
describe('RentalSettingsComponent', () => {
  const settings: RentalSettings = {
    overdueMultiplier: 0,
    depositEnabled: false,
    taxRate: 19,
    dayCutoffHour: 12,
    minimumDays: 0,
  };

  let rentalsApiStub: {
    getSettings: jasmine.Spy;
    getProductMultiplier: jasmine.Spy;
    updateSettings: jasmine.Spy;
  };
  /** The per-item list, a page at a time from the server (owner, 2026-11-17). */
  let pagesStub: { itemFiguresPage: jasmine.Spy };

  beforeEach(() => {
    rentalsApiStub = {
      getSettings: jasmine.createSpy('getSettings').and.resolveTo(settings),
      getProductMultiplier: jasmine.createSpy('getProductMultiplier').and.resolveTo({
        productId: 'p-9', sku: 'ECH-9', nameAr: 'سقالة', nameFr: 'Échafaudage', nameEn: 'Scaffold',
        overdueMultiplier: null, rentalUnitPrice: 250, rentalDamageUnitPrice: 4000,
      }),
      updateSettings: jasmine.createSpy('updateSettings').and.resolveTo(settings),
    };
    pagesStub = {
      itemFiguresPage: jasmine
        .createSpy('itemFiguresPage')
        .and.callFake(async (q: { take: number }) => ({ items: [], total: 0, skip: 0, take: q.take })),
    };

    TestBed.configureTestingModule({
      providers: [
        { provide: RentalsApi, useValue: rentalsApiStub },
        { provide: RentalPagesApi, useValue: pagesStub },
        { provide: ConfirmService, useValue: { ask: jasmine.createSpy('ask').and.resolveTo(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  it('allows saving when overdue multiplier is out of range, but blocks invalid tax rate', async () => {
    const api = TestBed.inject(RentalsApi);
    const loadedSettings = await api.getSettings();

    const fixture = TestBed.createComponent(RentalSettingsComponent);
    fixture.componentRef.setInput('settings', loadedSettings);
    fixture.detectChanges();
    await fixture.whenStable();

    const component = fixture.componentInstance;
    expect(component.canSave()).toBeTrue();

    const taxInput = fixture.nativeElement.querySelector('#rs-tax') as HTMLInputElement;
    taxInput.value = '150';
    taxInput.dispatchEvent(new Event('input'));
    if (component.taxRate() !== 150) {
      component.taxRate.set(150);
    }
    fixture.detectChanges();
    await fixture.whenStable();

    expect(component.canSave()).toBeFalse();
  });

  it('setting the minimum days field to 15 and saving sends minimumDays: 15 in the PUT body', async () => {
    const api = TestBed.inject(RentalsApi);
    const loadedSettings = await api.getSettings();

    const fixture = TestBed.createComponent(RentalSettingsComponent);
    fixture.componentRef.setInput('settings', loadedSettings);
    fixture.detectChanges();
    await fixture.whenStable();

    const component = fixture.componentInstance;
    expect(component.minDays()).toBe(0);

    const minDaysInput = fixture.nativeElement.querySelector('#rs-min-days') as HTMLInputElement;
    minDaysInput.value = '15';
    minDaysInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(component.minDays()).toBe(15);
    await fixture.whenStable();

    await component.save();

    expect(rentalsApiStub.updateSettings).toHaveBeenCalledWith(
      jasmine.objectContaining({ minimumDays: 15 }),
    );
  });

  it('asks the server for the per-item list, ten rows first, with its search and filters', async () => {
    const fixture = TestBed.createComponent(RentalSettingsComponent);
    fixture.componentRef.setInput('settings', settings);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(pagesStub.itemFiguresPage).toHaveBeenCalledWith({ skip: 0, take: 10, q: '', price: 'all', damage: 'all' });

    const component = fixture.componentInstance;
    component.overrideQuery.set(' سقاله ');
    component.priceFilter.set('set');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    expect(pagesStub.itemFiguresPage).toHaveBeenCalledWith({ skip: 0, take: 10, q: 'سقاله', price: 'set', damage: 'all' });
  });

  it('prefills a picked item from the server, whatever page it is on', async () => {
    const fixture = TestBed.createComponent(RentalSettingsComponent);
    fixture.componentRef.setInput('settings', settings);
    fixture.detectChanges();
    const component = fixture.componentInstance;

    await component.pickItem('p-9');
    expect(rentalsApiStub.getProductMultiplier).toHaveBeenCalledWith('p-9');
    expect(component.itemRentalUnitPrice()).toBe(250);
    expect(component.itemDamageValue()).toBe(4000);
    expect(component.itemMultiplier()).toBeNull();
  });
});
