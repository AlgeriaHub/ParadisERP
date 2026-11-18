import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { BranchFace } from '../../../core/branch-face';
import { BranchLock } from '../../../core/branch-lock';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { Branch } from '../../../core/models';
import { WorkingBranchComponent } from './working-branch.component';

/**
 * The header's branch block (D2): the Admin's working-branch menu, plain text for everyone else.
 * The choice is the working branch's alone — the dashboard keeps its own (owner, 2026-10-03).
 */
describe('the working-branch block', () => {
  const locked = signal(false);
  const branch = (id: string, nameFr: string, isMain = false, isActive = true) =>
    ({ id, nameFr, nameAr: nameFr, nameEn: nameFr, isMain, isActive }) as Branch;
  const all = signal<Branch[]>([branch('b-oran', 'Oran'), branch('b-alger', 'Dépôt central', true), branch('b-old', 'Fermée', false, false)]);
  const keys = ['pos.workingBranch.u-admin', 'pos.dashboardBranch.u-admin'];

  beforeEach(() => {
    locked.set(false);
    keys.forEach((k) => localStorage.removeItem(k));
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { currentUser: signal({ id: 'u-admin' }) } },
        {
          provide: BranchLock,
          useValue: {
            locked,
            lockedBranchId: () => (locked() ? 'b-oran' : null),
            ownBranchId: () => (locked() ? 'b-oran' : 'b-alger'),
          },
        },
        {
          provide: BranchFace,
          useValue: {
            all,
            name: signal('Oran'),
            nameOf: (id: string) => all().find((b) => b.id === id)?.nameFr ?? '',
          },
        },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  afterEach(() => keys.forEach((k) => localStorage.removeItem(k)));

  function render() {
    const fixture = TestBed.createComponent(WorkingBranchComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('gives the Admin a menu on their own branch at first: active branches, the warehouse first, then all branches', () => {
    const fixture = render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('app-menu')).not.toBeNull();
    expect(el.querySelector('.wb__text strong')!.textContent).toContain('Dépôt central');
    const items = [...el.querySelectorAll('.app-menu-item')].map((i) => i.textContent!.trim());
    expect(items.length).toBe(3);
    expect(items[0]).toContain('Dépôt central');
    expect(items[1]).toContain('Oran');
    expect(items[2]).toBe(TestBed.inject(I18nService).t('scope.all'));
  });

  it('moves the working branch, kept per user, and never the dashboard\'s own choice', () => {
    const fixture = render();
    fixture.componentInstance.choose({ kind: 'all' });
    fixture.detectChanges();
    expect(localStorage.getItem('pos.workingBranch.u-admin')).toBe('{"kind":"all"}');
    expect(localStorage.getItem('pos.dashboardBranch.u-admin')).toBeNull();
    expect(TestBed.inject(BranchScopeService).working()).toEqual({ kind: 'all' });
    expect((fixture.nativeElement as HTMLElement).querySelector('.wb__text strong')!.textContent).toContain(
      TestBed.inject(I18nService).t('scope.all'),
    );
  });

  it('leaves the working branch alone when the dashboard\'s choice is stored', () => {
    localStorage.setItem('pos.dashboardBranch.u-admin', '{"kind":"all"}');
    render();
    expect(TestBed.inject(BranchScopeService).working()).toEqual({ kind: 'branch', id: 'b-alger' });
  });

  it('shows a locked user their branch as plain text, with nothing to choose', () => {
    locked.set(true);
    const fixture = render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('app-menu')).toBeNull();
    expect(el.querySelector('.wb__plain strong')!.textContent).toContain('Oran');
  });
});
