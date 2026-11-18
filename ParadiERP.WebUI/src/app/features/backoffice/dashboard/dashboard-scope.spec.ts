import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { DashboardScope, dashboardScope } from './dashboard-scope';

/** The dashboard's own branch choice (owner, 2026-10-03): not the working branch, and kept apart from it. */
describe('the dashboard scope', () => {
  const two = [{ id: 'b-alger' }, { id: 'b-oran' }];
  const one = [{ id: 'b-alger' }];

  it('opens the Admin on every branch when the company has more than one', () => {
    expect(dashboardScope({ lockedId: null, stored: null, branches: two })).toEqual({ branchId: null, label: 'all' });
  });

  it('opens the Admin on the one branch when there is only one', () => {
    expect(dashboardScope({ lockedId: null, stored: null, branches: one })).toEqual({ branchId: 'b-alger', label: 'branch' });
  });

  it('counts only active branches when deciding the default', () => {
    const closed = [{ id: 'b-alger' }, { id: 'b-old', isActive: false }];
    expect(dashboardScope({ lockedId: null, stored: null, branches: closed })).toEqual({ branchId: 'b-alger', label: 'branch' });
  });

  it('keeps the Admin\'s own dashboard choice, branch or every branch', () => {
    expect(dashboardScope({ lockedId: null, stored: { kind: 'branch', id: 'b-oran' }, branches: two })).toEqual({ branchId: 'b-oran', label: 'branch' });
    expect(dashboardScope({ lockedId: null, stored: { kind: 'all' }, branches: one })).toEqual({ branchId: null, label: 'all' });
  });

  it('drops a remembered branch that is no longer the company\'s, back to the default', () => {
    expect(dashboardScope({ lockedId: null, stored: { kind: 'branch', id: 'b-gone' }, branches: two })).toEqual({ branchId: null, label: 'all' });
  });

  it('without the branch list, keeps a remembered choice and otherwise shows every branch', () => {
    expect(dashboardScope({ lockedId: null, stored: { kind: 'branch', id: 'b-oran' }, branches: null })).toEqual({ branchId: 'b-oran', label: 'branch' });
    expect(dashboardScope({ lockedId: null, stored: null, branches: null })).toEqual({ branchId: null, label: 'all' });
  });

  it('keeps a locked Manager on their own branch whatever is stored', () => {
    expect(dashboardScope({ lockedId: 'b-oran', stored: { kind: 'all' }, branches: two })).toEqual({ branchId: 'b-oran', label: 'branch' });
  });

  describe('the remembered choice', () => {
    const locked = signal(false);
    const user = signal<{ id: string } | null>({ id: 'u-admin' });

    beforeEach(() => {
      locked.set(false);
      user.set({ id: 'u-admin' });
      localStorage.removeItem('pos.dashboardBranch.u-admin');
      localStorage.removeItem('pos.workingBranch.u-admin');
      TestBed.configureTestingModule({
        providers: [
          { provide: AuthService, useValue: { currentUser: user } },
          {
            provide: BranchLock,
            useValue: { locked, lockedBranchId: () => (locked() ? 'b-oran' : null), ownBranchId: () => 'b-alger' },
          },
        ],
      });
    });

    afterEach(() => {
      localStorage.removeItem('pos.dashboardBranch.u-admin');
      localStorage.removeItem('pos.workingBranch.u-admin');
    });

    it('is kept across a reload, under its own key, and leaves the working branch untouched', () => {
      TestBed.inject(DashboardScope).set({ kind: 'all' });
      expect(localStorage.getItem('pos.dashboardBranch.u-admin')).toBe('{"kind":"all"}');
      expect(localStorage.getItem('pos.workingBranch.u-admin')).toBeNull();
      // Another page still starts on the Admin's own branch: the dashboard's "every branch" did not travel.
      expect(TestBed.inject(BranchScopeService).working()).toEqual({ kind: 'branch', id: 'b-alger' });

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          { provide: AuthService, useValue: { currentUser: user } },
          { provide: BranchLock, useValue: { locked, lockedBranchId: () => null, ownBranchId: () => 'b-alger' } },
        ],
      });
      expect(TestBed.inject(DashboardScope).stored()).toEqual({ kind: 'all' });
    });

    it('does not read another page\'s working branch as the dashboard\'s choice', () => {
      localStorage.setItem('pos.workingBranch.u-admin', '{"kind":"branch","id":"b-oran"}');
      expect(TestBed.inject(DashboardScope).stored()).toBeNull();
    });

    it('is neither offered to nor stored for a locked user', () => {
      locked.set(true);
      const scope = TestBed.inject(DashboardScope);
      expect(scope.canChoose()).toBeFalse();
      scope.set({ kind: 'all' });
      expect(localStorage.getItem('pos.dashboardBranch.u-admin')).toBeNull();
      expect(scope.lockedId()).toBe('b-oran');
    });
  });
});
