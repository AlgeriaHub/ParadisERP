import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminBranch, AdminDevice, AdminService, UpsertDeviceRequest } from '../../core/admin.service';
import { ApiError } from '../../core/api.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../ui/confirm.service';
import { AdminPagesApi, DevicesPageQuery } from './admin-pages.api';
import { BackofficeDevicesComponent } from './devices-tab.component';

/**
 * A warehouse holds the stock and sends it to the branches; it does not sell (the owner,
 * 2026-10-05). The till screen says so in words: a till already on a warehouse is flagged, a
 * warehouse cannot be picked for another till, and the server's refusal is read out rather than
 * shown as the raw reply.
 */
const WAREHOUSE: AdminBranch = {
  id: 'b-main', nameFr: 'Dépôt central', nameAr: 'المستودع المركزي', nameEn: 'Central depot',
  address: '', phone: '', isMain: true, isWarehouse: true, allowNegativeStock: true, isActive: true, version: 1,
};
const SHOP: AdminBranch = {
  ...WAREHOUSE, id: 'b-shop', nameFr: 'Ouled Moussa', nameAr: 'أولاد موسى', nameEn: 'Ouled Moussa', isMain: true, isWarehouse: false,
};

function device(over: Partial<AdminDevice>): AdminDevice {
  return {
    id: 'd-1', code: 'C01', name: 'Caisse 1', branchId: SHOP.id, branchName: SHOP.nameFr,
    isActive: true, lastSyncAtUtc: null, lastCursor: 0, onWarehouse: false, ...over,
  };
}

describe('BackofficeDevicesComponent and the main warehouse', () => {
  let fixture: ComponentFixture<BackofficeDevicesComponent>;
  let created: UpsertDeviceRequest[];
  let refuse: boolean;

  async function mount(devices: AdminDevice[]): Promise<void> {
    created = [];
    refuse = false;
    TestBed.configureTestingModule({
      imports: [BackofficeDevicesComponent],
      providers: [
        // The tills are paged by the server (owner, 2026-11-17).
        {
          provide: AdminPagesApi,
          useValue: {
            devicesPage: (q: DevicesPageQuery) =>
              Promise.resolve({ items: devices, total: devices.length, skip: 0, take: q.take }),
          },
        },
        {
          provide: AdminService,
          useValue: {
            listBranches: () => Promise.resolve([WAREHOUSE, SHOP]),
            createDevice: (r: UpsertDeviceRequest) => {
              if (refuse) {
                return Promise.reject(new ApiError(JSON.stringify({
                  status: 400, detail: 'The main warehouse does not sell: register this till on a selling branch.',
                  code: 'warehouse_till',
                }), 400));
              }
              created.push(r);
              return Promise.resolve(device({ code: r.code }));
            },
            updateDevice: () => Promise.reject(new Error('not used')),
          },
        },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    // The back-office words arrive with the shell's route chunk; load them as the shell does.
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BackofficeDevicesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const t = (key: string) => TestBed.inject(I18nService).t(key);

  it('flags a till that sits on the warehouse, in words, and only that one', async () => {
    await mount([
      device({ id: 'd-1', code: 'C01', branchId: WAREHOUSE.id, branchName: WAREHOUSE.nameFr, onWarehouse: true }),
      device({ id: 'd-2', code: 'C02' }),
    ]);
    const rows = Array.from(el().querySelectorAll('tbody tr'));
    expect(rows[0].textContent).toContain(t('backoffice.devices.onWarehouse'));
    expect(rows[0].textContent).toContain(t('backoffice.devices.onWarehouseHint'));
    expect(rows[0].querySelector('app-status')?.className).toContain('tone-warning');
    expect(rows[1].textContent).not.toContain(t('backoffice.devices.onWarehouse'));
    // The branch is named from the branch list in the reader's language (French here).
    expect(rows[0].textContent).toContain(WAREHOUSE.nameFr);
  });

  it('offers the warehouse to a new till only as a disabled, explained choice, and starts on a selling branch', async () => {
    await mount([]);
    // No till yet: the empty list offers the first one (the page header's button is drawn by the page).
    (el().querySelector('app-table-state .btn-primary') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const options = Array.from(el().querySelectorAll<HTMLOptionElement>('#dv-edit-branch option'));
    const warehouse = options.find((o) => o.textContent?.includes(WAREHOUSE.nameFr))!;
    const shop = options.find((o) => o.textContent?.includes(SHOP.nameFr))!;
    expect(warehouse.disabled).toBeTrue();
    expect(warehouse.textContent).toContain(t('backoffice.devices.warehouseOption'));
    expect(shop.disabled).toBeFalse();
    expect(fixture.componentInstance.form.branchId).toBe(SHOP.id);
  });

  it('keeps the warehouse choosable for a till already there, so it can be renamed or switched off', async () => {
    const onWarehouse = device({ branchId: WAREHOUSE.id, branchName: WAREHOUSE.nameFr, onWarehouse: true });
    await mount([onWarehouse]);
    fixture.componentInstance.edit(onWarehouse);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const warehouse = Array.from(el().querySelectorAll<HTMLOptionElement>('#dv-edit-branch option'))
      .find((o) => o.textContent?.includes(WAREHOUSE.nameFr))!;
    expect(warehouse.disabled).toBeFalse();
  });

  it("says the server's warehouse refusal in words, not as the raw reply", async () => {
    await mount([]);
    fixture.componentInstance.openCreate();
    refuse = true;
    fixture.componentInstance.form = { code: 'w01', name: '', branchId: WAREHOUSE.id, isActive: true };
    await fixture.componentInstance.save();
    fixture.detectChanges();
    expect(fixture.componentInstance.error()).toBe(t('backoffice.devices.warehouseRefused'));
    expect(el().textContent).not.toContain('"code"');
    expect(created.length).toBe(0);
  });
});
