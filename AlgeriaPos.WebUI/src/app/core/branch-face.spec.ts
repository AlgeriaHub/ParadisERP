import { faceBranchId, resolveBranchLogo } from './branch-face';

/**
 * "The branch name appears with its logo; if the branch has no logo, take the main branch's logo"
 * (the owner, 2026-09-30) — then the company's, then the text mark.
 */
describe('branch face', () => {
  const main = { id: 'douera', isMain: true, logoUrl: '/api/catalog/branches/douera/logo?v=main' as string | null };
  const skikda = { id: 'skikda', isMain: false, logoUrl: null as string | null };
  const oran = { id: 'oran', isMain: false, logoUrl: '/api/catalog/branches/oran/logo?v=oran' as string | null };
  const company = '/api/catalog/store/logo?v=company';

  describe('resolveBranchLogo', () => {
    it('shows the branch its own logo', () => {
      expect(resolveBranchLogo(oran, [main, skikda, oran], company)).toBe(oran.logoUrl);
    });

    it("falls back to the main branch's logo", () => {
      expect(resolveBranchLogo(skikda, [main, skikda, oran], company)).toBe(main.logoUrl);
    });

    it("falls back to the company's logo when the main branch has none", () => {
      const bare = { ...main, logoUrl: null };
      expect(resolveBranchLogo(skikda, [bare, skikda], company)).toBe(company);
    });

    it('leaves the text mark when there is no logo anywhere', () => {
      const bare = { ...main, logoUrl: null };
      expect(resolveBranchLogo(skikda, [bare, skikda], null)).toBeNull();
      expect(resolveBranchLogo(null, [], null)).toBeNull();
    });
  });

  describe('faceBranchId', () => {
    const all = [main, skikda, oran];

    it("puts a till's own branch in a cashier's bar", () => {
      expect(faceBranchId('Cashier', 'skikda', 'oran', all)).toBe('oran');
    });

    it('puts the branch a manager or an admin works at in theirs', () => {
      expect(faceBranchId('Manager', 'skikda', 'oran', all)).toBe('skikda');
      expect(faceBranchId('Admin', 'oran', null, all)).toBe('oran');
    });

    it('falls back to the main branch when neither is known', () => {
      expect(faceBranchId('Manager', null, null, all)).toBe('douera');
      expect(faceBranchId('Cashier', 'gone', 'gone', all)).toBe('douera');
      expect(faceBranchId(null, null, null, [])).toBeNull();
    });
  });
});
