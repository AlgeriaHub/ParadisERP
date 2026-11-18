import { WorkingBranch, parseWorking, resolveScope, startBranch } from './branch-scope.service';

/**
 * Decision D2 (REDESIGN_PLAN.md §8): the Admin's working branch only pre-fills, "All branches" is
 * offered only where the endpoint takes it, the ledger's null is the group and never "all", and
 * a locked user is never moved off their branch.
 */
describe('branch scope', () => {
  const one = { kind: 'branch' as const, id: 'oran' };
  const all = { kind: 'all' as const };

  it('keeps a locked user on their own branch whatever is stored', () => {
    expect(resolveScope(all, 'skikda', 'skikda', 'branchOrAll')).toEqual({ branchId: 'skikda', label: 'branch' });
    expect(resolveScope(one, 'skikda', 'skikda', 'branchOrGroup')).toEqual({ branchId: 'skikda', label: 'branch' });
  });

  it('says company-wide on company data, for everyone, and filters nothing', () => {
    expect(resolveScope(one, null, 'douera', 'company')).toEqual({ branchId: null, label: 'company' });
    expect(resolveScope(all, 'skikda', 'skikda', 'company')).toEqual({ branchId: null, label: 'company' });
  });

  it('starts a screen on the branch the Admin chose', () => {
    expect(resolveScope(one, null, 'douera', 'branchOrAll')).toEqual({ branchId: 'oran', label: 'branch' });
    expect(resolveScope(one, null, 'douera', 'oneBranch')).toEqual({ branchId: 'oran', label: 'branch' });
  });

  it('honours "All branches" only where the endpoint takes a null branch as no filter', () => {
    expect(resolveScope(all, null, 'douera', 'branchOrAll')).toEqual({ branchId: null, label: 'all' });
    expect(resolveScope(all, null, 'douera', 'oneBranch')).toEqual({ branchId: 'douera', label: 'branch' });
  });

  it('opens the ledger on the group, consolidated, never labelled "all branches"', () => {
    expect(resolveScope(all, null, 'douera', 'branchOrGroup')).toEqual({ branchId: null, label: 'group' });
  });

  it('reads anything malformed in storage as no choice', () => {
    expect(parseWorking('{"kind":"all"}')).toEqual({ kind: 'all' });
    expect(parseWorking('{"kind":"branch","id":"oran"}')).toEqual({ kind: 'branch', id: 'oran' });
    expect(parseWorking('{"kind":"branch"}')).toBeNull();
    expect(parseWorking('{"kind":"everything"}')).toBeNull();
    expect(parseWorking('not json')).toBeNull();
  });

  describe('a document form’s branch field (§8.1)', () => {
    const branches = [{ id: 'alger', isMain: true }, { id: 'oran' }];
    const scope = (w: WorkingBranch) => ({ working: () => w });
    const lock = (lockedId: string | null) => ({
      lockedBranchId: () => lockedId,
      // Today's default: the user's own branch when listed, else the main one.
      defaultBranch: () => lockedId ?? 'alger',
    });

    it('starts on the working branch when it names one the form lists', () => {
      expect(startBranch(scope(one), lock(null), branches)).toBe('oran');
    });

    it('falls back to today’s default on "all branches" or a branch the form does not list', () => {
      expect(startBranch(scope(all), lock(null), branches)).toBe('alger');
      expect(startBranch(scope({ kind: 'branch', id: 'gone' }), lock(null), branches)).toBe('alger');
    });

    it('keeps a locked user on their own branch, whatever the working branch says', () => {
      expect(startBranch(scope(one), lock('setif'), branches)).toBe('setif');
    });
  });
});
