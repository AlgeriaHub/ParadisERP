import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminPaymentMethod, AdminService } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { PaymentMethodsComponent } from './payment-methods.component';

describe('PaymentMethodsComponent', () => {
  let fixture: ComponentFixture<PaymentMethodsComponent>;
  let component: PaymentMethodsComponent;
  let adminSpy: jasmine.SpyObj<AdminService>;

  beforeAll(() => {
    if (typeof HTMLDialogElement !== 'undefined') {
      if (!HTMLDialogElement.prototype.showModal) {
        HTMLDialogElement.prototype.showModal = function (this: HTMLDialogElement) {
          this.open = true;
        };
      }
      if (!HTMLDialogElement.prototype.close) {
        HTMLDialogElement.prototype.close = function (this: HTMLDialogElement) {
          this.open = false;
        };
      }
    }
  });

  beforeEach(async () => {
    adminSpy = jasmine.createSpyObj<AdminService>('AdminService', [
      'listPaymentMethods',
      'createPaymentMethod',
      'updatePaymentMethod',
      'deletePaymentMethod',
    ]);
    adminSpy.listPaymentMethods.and.returnValue(Promise.resolve([]));
    adminSpy.createPaymentMethod.and.returnValue(Promise.resolve({} as AdminPaymentMethod));
    adminSpy.updatePaymentMethod.and.returnValue(Promise.resolve({} as AdminPaymentMethod));
    adminSpy.deletePaymentMethod.and.returnValue(Promise.resolve());

    await TestBed.configureTestingModule({
      imports: [PaymentMethodsComponent],
      providers: [
        { provide: AdminService, useValue: adminSpy },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    }).compileComponents();

    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);

    fixture = TestBed.createComponent(PaymentMethodsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
  });

  it('marks the key field as is-invalid, shows the required message, and does not call create API when saving with a blank key', async () => {
    component.openCreate();
    fixture.detectChanges();

    const keyInput = fixture.nativeElement.querySelector('#tr-key') as HTMLInputElement;
    const keyField = keyInput.closest('.bo-field');
    expect(keyField).toBeTruthy();
    expect(keyField?.classList.contains('is-invalid')).toBeFalse();
    expect(keyField?.querySelector('.bo-error')).toBeNull();

    await component.save();
    fixture.detectChanges();

    expect(adminSpy.createPaymentMethod).not.toHaveBeenCalled();
    expect(keyField?.classList.contains('is-invalid')).toBeTrue();

    const error = keyField?.querySelector('.bo-error');
    expect(error).toBeTruthy();
    expect(error?.getAttribute('role')).toBe('alert');
    expect(error?.textContent?.trim()).toBe(TestBed.inject(I18nService).t('common.fieldRequired'));
  });

  it('does not call create and marks the field invalid when the key has only whitespace', async () => {
    component.openCreate();
    component.form.key = '   ';
    fixture.detectChanges();

    await component.save();
    fixture.detectChanges();

    expect(adminSpy.createPaymentMethod).not.toHaveBeenCalled();
    const keyField = fixture.nativeElement.querySelector('#tr-key')?.closest('.bo-field');
    expect(keyField?.classList.contains('is-invalid')).toBeTrue();
    expect(keyField?.querySelector('.bo-error')).toBeTruthy();
  });

  it('calls create API when key is filled and closes editor', async () => {
    component.openCreate();
    component.form.key = 'virement-ccp';
    component.form.nameFr = 'Virement CCP';
    fixture.detectChanges();

    await component.save();
    fixture.detectChanges();

    expect(adminSpy.createPaymentMethod).toHaveBeenCalledWith(
      jasmine.objectContaining({ key: 'virement-ccp', nameFr: 'Virement CCP' }),
    );
    expect(component.editorOpen()).toBeFalse();
    expect(component.tried()).toBeFalse();
  });

  it('never shows the key error when editing an existing method', async () => {
    const existing: AdminPaymentMethod = {
      id: 'pm-1',
      key: 'cheque',
      nameAr: 'شيك',
      nameFr: 'Chèque',
      nameEn: 'Cheque',
      behavior: 'Generic',
      requiresConfirmation: true,
      affectsCashDrawer: false,
      requiresReference: true,
      requiresInstrumentDetails: true,
      isBuiltIn: false,
      isActive: true,
      sortOrder: 1,
    };

    component.edit(existing);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const keyInput = fixture.nativeElement.querySelector('#tr-key') as HTMLInputElement;
    const keyField = keyInput.closest('.bo-field');
    expect(keyInput.disabled).toBeTrue();

    // Even if form key were blank while editing, editing an existing method never shows the key error
    component.form.key = '';
    component.tried.set(true);
    fixture.detectChanges();

    expect(component.keyMissing()).toBeFalse();
    expect(keyField?.classList.contains('is-invalid')).toBeFalse();
    expect(keyField?.querySelector('.bo-error')).toBeNull();

    await component.save();

    expect(adminSpy.updatePaymentMethod).toHaveBeenCalledWith('pm-1', jasmine.objectContaining({ key: '' }));
    expect(adminSpy.createPaymentMethod).not.toHaveBeenCalled();
  });

  it('resets tried to false on openCreate, edit, and close', async () => {
    component.openCreate();
    await component.save();
    expect(component.tried()).toBeTrue();

    component.close();
    expect(component.tried()).toBeFalse();

    component.openCreate();
    await component.save();
    expect(component.tried()).toBeTrue();

    component.openCreate();
    expect(component.tried()).toBeFalse();

    await component.save();
    expect(component.tried()).toBeTrue();

    const existing: AdminPaymentMethod = {
      id: 'pm-2',
      key: 'cash',
      nameAr: 'نقد',
      nameFr: 'Espèces',
      nameEn: 'Cash',
      behavior: 'Cash',
      requiresConfirmation: false,
      affectsCashDrawer: true,
      requiresReference: false,
      requiresInstrumentDetails: false,
      isBuiltIn: true,
      isActive: true,
      sortOrder: 2,
    };
    component.edit(existing);
    expect(component.tried()).toBeFalse();
  });
});
