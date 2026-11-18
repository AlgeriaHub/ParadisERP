import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminBranch, AdminService, UpsertBranchRequest } from '../../core/admin.service';
import { ApiError } from '../../core/api.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../ui/confirm.service';
import { AdminPagesApi, BranchesPageQuery } from './admin-pages.api';
import { BackofficeBranchesComponent } from './branches-tab.component';

/**
 * Locations are a branch or a warehouse, and of each kind one is primary (the owner, 2026-10-05).
 * The form asks for the kind, offers "Set as primary" for both, says before saving whose flag it
 * will take, and will not let the only primary drop it; the list says the kind and "Primary" in words.
 */
const place = (id: string, nameFr: string, isWarehouse: boolean, isMain: boolean, code: string): AdminBranch => ({
  id, nameAr: nameFr, nameFr, nameEn: nameFr, address: '', phone: '',
  isMain, isWarehouse, allowNegativeStock: false, isActive: true, version: 1, code,
} as AdminBranch);

const SIEGE = place('siege', 'Siège', false, true, '361');
const ORAN = place('oran', 'Oran', false, false, '498');
const DEPOT = place('depot', 'Dépôt', true, true, '635');

describe('BackofficeBranchesComponent — kinds and primaries', () => {
  let fixture: ComponentFixture<BackofficeBranchesComponent>;
  let saved: UpsertBranchRequest[];
  let refusal: unknown;

  beforeEach(async () => {
    saved = [];
    refusal = null;
    TestBed.configureTestingModule({
      imports: [BackofficeBranchesComponent],
      providers: [
        // The locations are paged by the server, with the main one of each kind beside the page (owner, 2026-11-17).
        {
          provide: AdminPagesApi,
          useValue: {
            branchesPage: (q: BranchesPageQuery) =>
              Promise.resolve({ items: [SIEGE, ORAN, DEPOT], total: 3, skip: 0, take: q.take, summary: { mains: [SIEGE, DEPOT] } }),
          },
        },
        {
          provide: AdminService,
          useValue: {
            getStore: () => Promise.resolve({ fiscalIds: {} }),
            createBranch: (r: UpsertBranchRequest) => (saved.push(r), refusal ? Promise.reject(refusal) : Promise.resolve()),
            updateBranch: (_id: string, r: UpsertBranchRequest) => (saved.push(r), refusal ? Promise.reject(refusal) : Promise.resolve()),
          },
        },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BackofficeBranchesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  const i18n = () => TestBed.inject(I18nService);
  const el = () => fixture.nativeElement as HTMLElement;
  const primaryBox = () => el().querySelector('#br-main') as HTMLInputElement;

  it('lists every location with its kind and a Primary badge in words', () => {
    const kinds = [...el().querySelectorAll(`td[data-label="${i18n().t('backoffice.branches.kind')}"]`)].map((td) =>
      td.textContent?.replace(/\s+/g, ' ').trim(),
    );
    const primary = i18n().t('backoffice.branches.primary');
    expect(kinds).toEqual([
      `${i18n().t('backoffice.branches.kind.branch')} — ${primary}`,
      i18n().t('backoffice.branches.kind.branch'),
      `${i18n().t('backoffice.branches.kind.warehouse')} — ${primary}`,
    ]);
  });

  it('says before saving that ticking primary takes the flag from the current one of that kind', async () => {
    const c = fixture.componentInstance;
    c.edit(ORAN);
    fixture.detectChanges();
    expect(primaryBox().checked).toBeFalse();
    expect(primaryBox().disabled).toBeFalse();

    c.form.isMain = true;
    fixture.detectChanges();
    expect(el().querySelector('#br-main-hint')?.textContent).toContain(
      i18n().t('backoffice.branches.mainMoves.branch', { name: 'Siège' }),
    );
    await c.save();
    expect(saved[0].isMain).toBeTrue();
    expect(saved[0].isWarehouse).toBeFalse();
  });

  it('shows the current primary ticked and locked, with the kind fixed', async () => {
    fixture.componentInstance.edit(DEPOT);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(primaryBox().checked).toBeTrue();
    expect(primaryBox().disabled).toBeTrue();
    expect((el().querySelector('#br-kind-branch') as HTMLInputElement).disabled).toBeTrue();
    expect(el().querySelector('#br-main-hint')?.textContent).toContain(i18n().t('backoffice.branches.mainLocked.warehouse'));
  });

  it('starts a new location as an ordinary branch and unticks primary when the kind changes', () => {
    const c = fixture.componentInstance;
    c.openCreate();
    expect(c.form.isWarehouse).toBeFalse();
    expect(c.form.isMain).toBeFalse();
    c.form.isMain = true;
    c.setKind(true);
    expect(c.form.isWarehouse).toBeTrue();
    expect(c.form.isMain).toBeFalse();
  });

  it('says a server refusal in words', async () => {
    refusal = new ApiError(JSON.stringify({ status: 409, code: 'warehouse_has_stock' }), 409);
    const c = fixture.componentInstance;
    c.edit(ORAN);
    await c.save();
    expect(c.error()).toBe(i18n().t('backoffice.branches.error.warehouse_has_stock'));
  });

  // ---------- the location's code (owner, 2026-10-05): three digits, drawn by the server, fixed ----------

  it('lists every location with its code, as one left-to-right run', () => {
    const cells = [...el().querySelectorAll(`td[data-label="${i18n().t('backoffice.branches.code')}"]`)];
    expect(cells.map((td) => td.textContent?.trim())).toEqual(['361', '498', '635']);
    for (const td of cells) {
      expect(td.classList).toContain('mono');
      expect(td.querySelector('[dir="ltr"]')).not.toBeNull();
    }
  });

  it('shows the code read-only when editing: there is no field to type one in', () => {
    fixture.componentInstance.edit(ORAN);
    fixture.detectChanges();
    expect(el().querySelector('#br-code-fixed')?.textContent?.trim()).toBe('498');
    expect(el().querySelector('#br-code-fixed')?.classList).toContain('mono');
    expect(el().querySelector('input[id^="br-code"]')).toBeNull();
  });

  it('says a new location is given its code on save, and sends none', async () => {
    const c = fixture.componentInstance;
    c.openCreate();
    fixture.detectChanges();
    expect(el().querySelector('#br-code-new')?.textContent?.trim()).toBe(i18n().t('backoffice.branches.code.onSave'));
    expect(el().querySelector('#br-code-hint')?.textContent?.trim()).toBe(i18n().t('backoffice.branches.code.hint'));
    expect(el().querySelector('#br-code-fixed')).toBeNull();
    c.form.nameFr = 'Constantine';
    await c.save();
    expect('code' in saved[0]).toBeFalse();
  });

  it('says in words that every code from 100 to 999 is taken', async () => {
    refusal = new ApiError(
      JSON.stringify({ status: 409, code: 'branch_codes_exhausted', detail: 'All 900 branch codes (100–999) are in use.' }),
      409,
    );
    const c = fixture.componentInstance;
    c.openCreate();
    c.form.nameFr = 'Constantine';
    await c.save();
    expect(c.error()).toBe(i18n().t('backoffice.branches.error.branch_codes_exhausted'));
    for (const lang of ['fr', 'ar', 'en'] as const) {
      expect(i18n().tIn(lang, 'backoffice.branches.error.branch_codes_exhausted')).toContain('100');
      expect(i18n().tIn(lang, 'backoffice.branches.error.branch_codes_exhausted')).toContain('999');
    }
  });
});
