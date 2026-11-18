import { VIEW_OTHER_BRANCH_STOCK, defaultBranchId, mayLookUpOtherBranches, scopeBranches, scopeRowsTo } from './branch-lock';

/**
 * "The branch choice does not appear except for the super admin" (the owner, 2026-09-30). The
 * screens build every branch select from these two helpers, so they are pinned here.
 */
describe('branch lock', () => {
  const douera = { id: 'douera', isMain: true };
  const skikda = { id: 'skikda', isMain: false };
  const oran = { id: 'oran', isMain: false };
  const all = [douera, skikda, oran];

  describe('scopeBranches', () => {
    it('leaves every branch to a user who is not locked (the Admin)', () => {
      expect(scopeBranches(all, null)).toEqual(all);
    });

    it('offers a locked user only their own branch', () => {
      expect(scopeBranches(all, 'skikda')).toEqual([skikda]);
    });

    it('confines a manager of the main branch to it as well: the main branch grants nothing', () => {
      expect(scopeBranches(all, 'douera')).toEqual([douera]);
    });

    it('offers nothing rather than everything when the locked branch is not in the list', () => {
      expect(scopeBranches(all, 'gone')).toEqual([]);
    });
  });

  describe('scopeRowsTo', () => {
    const boxes = [
      { id: 'safe', branchId: 'douera' },
      { id: 'till-skikda', branchId: 'skikda' },
    ];

    it('keeps every box for the Admin and only their own for a locked user', () => {
      expect(scopeRowsTo(boxes, null)).toEqual(boxes);
      expect(scopeRowsTo(boxes, 'skikda').map((b) => b.id)).toEqual(['till-skikda']);
    });
  });

  describe('mayLookUpOtherBranches', () => {
    it('opens the other branches to the Admin, and to anyone else only with the grant', () => {
      expect(mayLookUpOtherBranches(false, [])).toBeTrue();
      expect(mayLookUpOtherBranches(true, [])).toBeFalse();
      expect(mayLookUpOtherBranches(true, ['payroll.manage'])).toBeFalse();
      expect(mayLookUpOtherBranches(true, [VIEW_OTHER_BRANCH_STOCK])).toBeTrue();
    });
  });

  describe('defaultBranchId', () => {
    it('starts a locked user on their branch, whatever the list holds', () => {
      expect(defaultBranchId(all, 'skikda', 'skikda')).toBe('skikda');
      expect(defaultBranchId([], 'skikda', 'skikda')).toBe('skikda');
    });

    it('starts the Admin on his own branch and lets the list follow otherwise', () => {
      expect(defaultBranchId(all, null, 'oran')).toBe('oran');
      expect(defaultBranchId(all, null, null)).toBe('douera');
      expect(defaultBranchId(all, null, 'unknown')).toBe('douera');
      // A stale session with no branch: the main branch, wherever it sits in the list.
      expect(defaultBranchId([oran, douera], null, null)).toBe('douera');
    });

    it('falls back when there is nothing to choose from', () => {
      expect(defaultBranchId([], null, null, 'x')).toBe('x');
      expect(defaultBranchId([], null, null)).toBeNull();
    });
  });
});
