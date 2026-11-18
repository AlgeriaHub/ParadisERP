import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminService } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { HrAttendanceImportComponent } from './attendance-import.component';
import { HrApi, PunchPreview, PunchPreviewQuery } from './hr-api.service';

/**
 * FR-3B: the punch-file preview. It reads and shows; the only call it can make is the preview,
 * which writes nothing — so the API stand-in below has no other method, and any write would fail
 * the test.
 */
describe('attendance import — the preview page', () => {
  const BRANCHES = [
    { id: 'b-main', nameFr: 'Dépôt central', isMain: true, isActive: true },
    { id: 'b-oran', nameFr: 'Oran', isMain: false, isActive: true },
  ];
  let calls: PunchPreviewQuery[];
  let answers: ((value: PunchPreview) => void)[];
  let locked: string | null;
  let listBranches: () => Promise<typeof BRANCHES>;

  function preview(over: Partial<PunchPreview> = {}): PunchPreview {
    return {
      fileName: 'sept.xlsx',
      sheetName: 'Sept',
      sheetNames: ['Sept'],
      headerRow: 1,
      suggestedHeaderRow: 1,
      dateOrder: 'DayMonthYear',
      columns: [
        { index: 1, letter: 'A', header: 'No.', samples: ['7'] },
        { index: 2, letter: 'B', header: 'Date', samples: ['01/09/2026'] },
        { index: 3, letter: 'C', header: 'Heure', samples: ['08:00'] },
      ],
      top: [{ row: 1, cells: { '1': 'No.', '2': 'Date', '3': 'Heure' } }],
      mappingProblem: 'mapNumber',
      summary: null,
      rows: [],
      rowsCut: false,
      employees: [],
      unknownNumbers: [],
      otherBranchNumbers: [],
      ...over,
    };
  }

  beforeEach(() => {
    calls = [];
    answers = [];
    locked = null;
    listBranches = () => Promise.resolve(BRANCHES);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: HrApi,
          useValue: {
            previewPunches: (q: PunchPreviewQuery) => {
              calls.push(q);
              return new Promise<PunchPreview>((resolve) => answers.push(resolve));
            },
          },
        },
        { provide: AdminService, useValue: { listBranches: () => listBranches() } },
        { provide: AuthService, useValue: { currentUser: signal({ role: 'Admin', permissions: [] }) } },
        {
          provide: BranchLock,
          useValue: {
            locked: () => locked !== null,
            lockedBranchId: () => locked,
            defaultBranch: () => 'b-main',
            ownBranchName: () => 'Oran',
          },
        },
        { provide: BranchScopeService, useValue: { working: () => ({ kind: 'all' }) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  async function render() {
    const fixture = TestBed.createComponent(HrAttendanceImportComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  function choose(page: HrAttendanceImportComponent, name = 'sept.xlsx') {
    const file = new File(['x'], name);
    page.pick({ target: { files: [file] } } as unknown as Event);
    return file;
  }

  it('reads the chosen file for the branch, with nothing mapped yet', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    const file = choose(page);

    expect(calls.length).toBe(1);
    expect(calls[0].file).toBe(file);
    expect(calls[0].branchId).toBe('b-main');
    expect(calls[0].header).toBeNull();
    expect(calls[0].mapping).toEqual({ number: null, name: null, date: null, time: null, dateTime: null, marker: null, timeOut: null });
  });

  it('reads the file again with each column chosen, and says what is still missing', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    choose(page);
    answers[0](preview());
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(TestBed.inject(I18nService).t('punch.problem.mapNumber'));
    page.setRole('number', 1);
    page.setRole('date', 2);
    page.setRole('time', 3);

    expect(calls.length).toBe(4);
    expect(calls[3].mapping).toEqual({ number: 1, name: null, date: 2, time: 3, dateTime: null, marker: null, timeOut: null });
  });

  it('shows only the latest answer when two cross', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    choose(page);
    page.setRole('number', 1);
    answers[1](preview({ fileName: 'latest.xlsx', mappingProblem: 'mapWhen' }));
    answers[0](preview({ fileName: 'stale.xlsx' }));
    await fixture.whenStable();

    expect(page.preview()?.fileName).toBe('latest.xlsx');
  });

  it('a new file forgets the last file\'s layout', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    choose(page);
    page.setHeader(0);
    page.setRole('number', 1);
    choose(page, 'oct.xlsx');

    const last = calls[calls.length - 1];
    expect(last.header).toBeNull();
    expect(last.mapping.number).toBeNull();
  });

  it('a locked manager reads their own branch, with no branch to choose', async () => {
    locked = 'b-oran';
    const fixture = await render();
    choose(fixture.componentInstance);

    expect(calls[0].branchId).toBe('b-oran');
    expect(fixture.nativeElement.querySelector('#pi-branch')).toBeNull();
    expect(fixture.nativeElement.querySelector('#pi-branch-fixed')?.textContent).toContain('Oran');
  });

  it('offers no button that writes attendance', async () => {
    const fixture = await render();
    choose(fixture.componentInstance);
    answers[0](
      preview({
        mappingProblem: null,
        summary: { rows: 1, matched: 1, unknown: 0, otherBranch: 0, invalid: 0, textDates: 1, first: '2026-09-01T08:00:00', last: '2026-09-01T08:00:00' },
        rows: [{ row: 2, status: 'matched', problem: null, number: '7', name: null, at: '2026-09-01T08:00:00', marker: null, source: '', employeeId: 'e', employeeNumber: 'EMP-000001', employeeName: 'Ali' }],
      }),
    );
    await fixture.whenStable();
    fixture.detectChanges();

    const buttons = Array.from(fixture.nativeElement.querySelectorAll('button.btn-primary'));
    expect(buttons.length).toBe(0);
    expect(fixture.nativeElement.textContent).toContain('01/09/2026 08:00:00');
  });
  it('a file chosen before the branch list arrives is read once it does', async () => {
    let release!: (b: typeof BRANCHES) => void;
    listBranches = () => new Promise((r) => (release = r));
    const fixture = TestBed.createComponent(HrAttendanceImportComponent);
    fixture.detectChanges();
    choose(fixture.componentInstance);
    expect(calls.length).toBe(0);

    release(BRANCHES);
    await fixture.whenStable();
    expect(calls.length).toBe(1);
    expect(calls[0].branchId).toBe('b-main');
  });
});
